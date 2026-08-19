using OpenCvSharp;

namespace Endoscopy.Services;

/// <summary>"Stop" isteğiyle (kullanıcı tetiklemesiyle) düzgün durdurulan kaydın sonucu.</summary>
public record VideoStopResult(string FilePath, bool IsPlaybackVerified, int Width, int Height, long FrameCount, long FileSizeBytes);

/// <summary>
/// Kaydın kullanıcı isteği OLMADAN (kamera koptu, disk doldu vb.) otomatik
/// durdurulduğunu bildirir. Bu durumda çağrıyı yapan bir HTTP isteği yok,
/// bu yüzden sonucu event üzerinden dışarı veriyoruz.
/// </summary>
public record VideoAutoStopInfo(string FilePath, bool IsPlaybackVerified, string Reason, int Width, int Height, long FrameCount, long FileSizeBytes);

/// <summary>
/// Bilgisayarın dahili/USB kamerasını arka planda sürekli okuyan servis.
/// Endoskopi kamerasının USB üzerinden RAM'e sürekli görüntü akıtması ile
/// birebir aynı mantığı temsil eder: bu servis "her zaman en son kareyi
/// bellekte hazır tutan" katmandır. Video akışı (video-feed) ve
/// yakalama (capture) endpoint'leri buradaki aynı kareyi okur.
/// </summary>
public class CameraService : IHostedService, IDisposable
{
    // Kayıt sürerken art arda bu kadar milisaniye boyunca kameradan kare
    // gelmezse (USB koptu, cihaz kapandı vb.) kaydı otomatik durduruyoruz —
    // aksi halde DB'de "recording" görünmeye devam eder ama içerik üretilmiyor olur.
    private const int CameraDropoutThresholdMs = 3000;

    // Kayıt sürerken bu aralıkla (her karede değil) diskteki boş alanı kontrol ediyoruz.
    private static readonly TimeSpan DiskSpaceCheckInterval = TimeSpan.FromSeconds(5);

    // Bu eşiğin altına inen boş disk alanında ne yeni kayıt başlatılır ne de
    // devam eden kayıt sürdürülür. 500MB, birkaç dakikalık kaydı bile tamamlamaya
    // yetmeyebilecek kritik bir seviye — kaydı yarıda bırakmaktansa önceden kesiyoruz.
    private const long MinFreeBytesForRecording = 500L * 1024 * 1024;

    private readonly ILogger<CameraService> _logger;
    private VideoCapture? _capture;
    private readonly Mat _latestFrame = new();
    private readonly object _lock = new();
    private Thread? _captureThread;
    private volatile bool _running;

    // --- Video kaydı ---
    // _writerLock hiçbir zaman _lock ile iç içe (nested) alınmaz; CaptureLoop
    // önce _lock'u bırakıp sonra _writerLock'u alır. Bu sıra, iki farklı thread'in
    // (HTTP isteği vs capture loop) ters sırayla kilit almasını ve deadlock'u önler.
    private readonly object _writerLock = new();
    private VideoWriter? _videoWriter;
    private volatile bool _isRecording;
    private string? _currentFilePath;
    private int _recordingWidth;
    private int _recordingHeight;

    // Kayıt başladığında sıfırlanır, her gerçek Write() çağrısında bir artar.
    // "Deklare edilen fps" ile "gerçekte üretilen kare sayısı" farklı olabiliyor
    // (kameranın gerçek hızı 20fps varsayımından sapabilir); bu sayaç, kayıt
    // bitince gerçek ortalama fps'i (FrameCount / süre) hesaplamaya yarar.
    private long _writtenFrameCount;

    // Codec tespiti bir kere (ilk kayıt başladığında) yapılıp önbelleğe alınır.
    // "IsOpened()==true" tek başına güvenilir değil: bu makinede avc1/H264
    // denendiğinde IsOpened() true dönebiliyor ama arkadaki encoder DLL'i
    // eksik/uyumsuzsa Write() sessizce hiçbir şey yazmıyor. Bu yüzden gerçekten
    // kare yazılıp yazılmadığını "yaz sonra geri oku" ile doğruluyoruz.
    private bool _codecDetected;
    private string _codecFourcc = "mp4v";
    private string _codecExtension = "mp4";
    private bool _codecIsBrowserCompatible;

    /// <summary>Bir önceki StartRecording çağrısı neden başarısız oldu (varsa) — hata mesajını netleştirmek için.</summary>
    public string? LastStartFailureReason { get; private set; }

    /// <summary>
    /// Kayıt, kullanıcı "stop" demeden (kamera koptu, disk doldu vb.) kendi
    /// kendine durduğunda tetiklenir. Abone olan taraf (bkz. Program.cs) bunu
    /// DB'de "interrupted" olarak işaretlemek için kullanır.
    /// </summary>
    public event Action<VideoAutoStopInfo>? RecordingAutoStopped;

    public CameraService(ILogger<CameraService> logger)
    {
        _logger = logger;
    }

    /// <summary>Kamera açık ve görüntü okuyabiliyor mu?</summary>
    public bool IsCameraAvailable => _capture is { } c && c.IsOpened();

    /// <summary>Şu anda bir video kaydı devam ediyor mu?</summary>
    public bool IsRecording => _isRecording;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 0 = bilgisayara bağlı ilk kamera (dahili webcam ya da tek USB kamera)
        _capture = new VideoCapture(0);

        if (!_capture.IsOpened())
        {
            _logger.LogWarning("Kamera açılamadı (index 0). Başka bir uygulama kamerayı kullanıyor olabilir ya da kamera bulunamadı.");
        }
        else
        {
            _logger.LogInformation("Kamera başarıyla açıldı, canlı akış başlıyor.");
        }

        _running = true;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "CameraCaptureLoop" };
        _captureThread.Start();

        return Task.CompletedTask;
    }

    private void CaptureLoop()
    {
        const int sleepMs = 30;
        var cameraDropoutThresholdTurns = CameraDropoutThresholdMs / sleepMs;

        using var frame = new Mat();
        var consecutiveFailedReads = 0;
        var lastDiskCheckAt = DateTimeOffset.MinValue;

        while (_running)
        {
            var readOk = _capture is { } cap && cap.IsOpened() && cap.Read(frame) && !frame.Empty();

            if (!readOk)
            {
                consecutiveFailedReads++;

                // Kayıt sürerken kamera bir süredir hiç kare vermiyorsa (USB koptu,
                // cihaz kapandı vb.) kaydı burada, otomatik olarak durduruyoruz —
                // aksi halde DB'de "recording" görünmeye devam eder ama içerik üretilmez.
                if (_isRecording && consecutiveFailedReads == cameraDropoutThresholdTurns)
                {
                    AutoStopRecording($"kamera yaklaşık {CameraDropoutThresholdMs / 1000.0:0.#} saniyedir kare vermiyor");
                }

                Thread.Sleep(sleepMs);
                continue;
            }

            consecutiveFailedReads = 0;
            lock (_lock)
            {
                frame.CopyTo(_latestFrame);
            }

            // Kayıt açıksa her kareyi anında dosyaya yaz. Bu sayede kayıt
            // yarıda kesilse (uygulama çökse) bile o ana kadar yazılmış
            // kısım diskte kalır — "sonda tek seferde yaz" değil, "üretildikçe yaz".
            var shouldCheckDiskSpace = false;
            if (_isRecording)
            {
                lock (_writerLock)
                {
                    if (_isRecording)
                    {
                        _videoWriter?.Write(frame);
                        _writtenFrameCount++;

                        if (DateTimeOffset.UtcNow - lastDiskCheckAt >= DiskSpaceCheckInterval)
                        {
                            shouldCheckDiskSpace = true;
                        }
                    }
                }
            }

            // Disk kontrolünü kilidin DIŞINDA yapıyoruz (dosya sistemi çağrısı,
            // gereksiz yere writer'ı bloklamasın); AutoStopRecording kendi kilidini
            // kendi açıp kapatıyor.
            if (shouldCheckDiskSpace)
            {
                lastDiskCheckAt = DateTimeOffset.UtcNow;
                var dir = _currentFilePath != null ? Path.GetDirectoryName(_currentFilePath) : null;

                if (dir != null && GetAvailableFreeBytes(dir) < MinFreeBytesForRecording)
                {
                    AutoStopRecording("disk alanı kritik seviyenin altına düştü");
                }
            }

            Thread.Sleep(sleepMs);
        }
    }

    /// <summary>
    /// Video kaydını başlatır: "storageDir/baseFileName.{ext}" dosyasını açar.
    /// Kayıt boyunca gelen her kare bu tek dosyaya, üretildiği anda yazılır.
    /// İlk çağrıda hangi codec'in bu makinede gerçekten çalıştığı tespit edilip
    /// önbelleğe alınır (bkz. DetectWorkingCodec). Başarılıysa gerçek dosya
    /// yolunu, kamera/codec hatasında null döner.
    /// </summary>
    public string? StartRecording(string storageDir, string baseFileName, int fps = 20)
    {
        LastStartFailureReason = null;

        int width, height;
        lock (_lock)
        {
            if (_latestFrame.Empty())
            {
                LastStartFailureReason = "Kameradan henüz bir görüntü karesi alınamadı.";
                return null;
            }
            width = _latestFrame.Width;
            height = _latestFrame.Height;
        }

        if (GetAvailableFreeBytes(storageDir) < MinFreeBytesForRecording)
        {
            LastStartFailureReason = $"Disk alanı yetersiz (en az {MinFreeBytesForRecording / (1024 * 1024)} MB boş alan gerekiyor).";
            _logger.LogError("Video kaydı başlatılamadı: {Reason}", LastStartFailureReason);
            return null;
        }

        if (!_codecDetected)
        {
            DetectWorkingCodec();
        }

        lock (_writerLock)
        {
            if (_isRecording)
            {
                LastStartFailureReason = "Zaten devam eden bir kayıt var.";
                return null;
            }

            var size = new OpenCvSharp.Size(width, height);
            var path = Path.Combine(storageDir, $"{baseFileName}.{_codecExtension}");
            var writer = new VideoWriter(path, VideoWriter.FourCC(_codecFourcc[0], _codecFourcc[1], _codecFourcc[2], _codecFourcc[3]), fps, size);

            if (!writer.IsOpened())
            {
                writer.Dispose();
                LastStartFailureReason = $"Önceden doğrulanmış codec ({_codecFourcc}) bu sefer açılamadı.";
                _logger.LogError("Video kaydı başlatılamadı: {Reason} Path={Path}", LastStartFailureReason, path);
                return null;
            }

            _videoWriter = writer;
            _currentFilePath = path;
            _recordingWidth = width;
            _recordingHeight = height;
            _writtenFrameCount = 0;
            _isRecording = true;

            _logger.LogInformation(
                "Video kaydı başladı: {Path} ({Width}x{Height}, {Fps}fps, codec={Fourcc}, tarayıcı-uyumlu={BrowserCompatible})",
                path, width, height, fps, _codecFourcc, _codecIsBrowserCompatible);

            return path;
        }
    }

    /// <summary>
    /// Kullanıcı isteğiyle (Stop butonu) devam eden video kaydını durdurur, dosyayı
    /// kapatır (finalize eder) ve dosyanın gerçekten oynatılabilir olup olmadığını
    /// doğrular. Aktif kayıt yoksa (örn. az önce otomatik durmuşsa) null döner.
    /// </summary>
    public VideoStopResult? StopRecording()
    {
        var closed = CloseCurrentRecordingFile();
        if (closed == null) return null;

        var (filePath, verified, width, height, frameCount, fileSizeBytes) = closed.Value;
        _logger.LogInformation(
            "Video kaydı durduruldu: {Path} ({Width}x{Height}, {FrameCount} kare, {FileSizeBytes} byte, oynatma doğrulaması: {Verified})",
            filePath, width, height, frameCount, fileSizeBytes, verified ? "başarılı" : "BAŞARISIZ");

        return new VideoStopResult(filePath, verified, width, height, frameCount, fileSizeBytes);
    }

    /// <summary>
    /// Kaydı kullanıcı isteği OLMADAN durdurur (kamera koptu / disk doldu).
    /// _writerLock tutulmuyorken çağrılmalı (CaptureLoop'ta zaten öyle çağrılıyor).
    /// </summary>
    private void AutoStopRecording(string reason)
    {
        var closed = CloseCurrentRecordingFile();
        if (closed == null) return;

        var (filePath, verified, width, height, frameCount, fileSizeBytes) = closed.Value;
        _logger.LogError(
            "Video kaydı otomatik durduruldu ({Reason}): {Path} ({Width}x{Height}, {FrameCount} kare, {FileSizeBytes} byte, oynatma doğrulaması: {Verified})",
            reason, filePath, width, height, frameCount, fileSizeBytes, verified ? "başarılı" : "BAŞARISIZ");

        RecordingAutoStopped?.Invoke(new VideoAutoStopInfo(filePath, verified, reason, width, height, frameCount, fileSizeBytes));
    }

    /// <summary>
    /// Ortak kapatma mantığı: writer'ı finalize eder, dosyayı geri okuyarak
    /// doğrular ve gerçek dosya boyutunu ölçer (video sürerken boyut hâlâ
    /// büyüyor olacağından, ancak dosya kapandıktan sonra kesin/nihai olur).
    /// Kayıt zaten durmuşsa null döner (iki taraf aynı anda durdurmaya
    /// çalışırsa ikinci çağıran bunu anlar).
    /// </summary>
    private (string FilePath, bool Verified, int Width, int Height, long FrameCount, long FileSizeBytes)? CloseCurrentRecordingFile()
    {
        string? filePath;
        int width, height;
        long frameCount;
        lock (_writerLock)
        {
            if (!_isRecording) return null;

            _isRecording = false;
            _videoWriter?.Release();
            _videoWriter?.Dispose();
            _videoWriter = null;
            filePath = _currentFilePath;
            _currentFilePath = null;
            width = _recordingWidth;
            height = _recordingHeight;
            frameCount = _writtenFrameCount;
        }

        if (filePath == null) return null;

        var verified = VerifyRecordedFile(filePath);

        long fileSizeBytes = 0;
        try
        {
            if (File.Exists(filePath)) fileSizeBytes = new FileInfo(filePath).Length;
        }
        catch { /* boyut okunamazsa 0 kalsın, kritik bir bilgi değil */ }

        return (filePath, verified, width, height, frameCount, fileSizeBytes);
    }

    /// <summary>
    /// Kayıt tamamlandıktan sonra dosyanın gerçekten açılıp en az bir kare
    /// verebildiğini kontrol eder — codec tespitinde kullandığımız round-trip
    /// mantığının aynısı, ama bu sefer sentetik test dosyası değil, GERÇEK
    /// kaydedilen dosya üzerinde. Sadece ilk kareyi okuyor (uzun kayıtlarda
    /// tüm dosyayı okumak yavaş olur); bu bir "hafif sağlık kontrolü" — dosyanın
    /// ortasında/sonunda oluşabilecek bozulmaları yakalamaz, ama en yaygın
    /// senaryoyu (encoder hiç çalışmadı, dosya baştan boş/bozuk) yakalar.
    /// </summary>
    private bool VerifyRecordedFile(string path)
    {
        try
        {
            using var reader = new VideoCapture(path);
            if (!reader.IsOpened()) return false;

            using var frame = new Mat();
            return reader.Read(frame) && !frame.Empty();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"path"in bulunduğu sürücüdeki boş alanı byte cinsinden döner.</summary>
    private long GetAvailableFreeBytes(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return long.MaxValue; // sürücü tespit edilemiyorsa engelleme

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            // Disk bilgisini okuyamıyorsak (ör. ağ sürücüsü, izin hatası) kaydı
            // bu kontrol yüzünden engellemiyoruz — sessizce "bol yer var" varsayıyoruz.
            return long.MaxValue;
        }
    }

    /// <summary>
    /// İlk kayıt başlatıldığında bir kere çalışır: hangi video codec'inin bu
    /// makinede GERÇEKTEN çalıştığını tespit edip önbelleğe alır.
    ///   1) avc1/H264 (H.264) — tarayıcıların native &lt;video&gt; ile oynattığı codec.
    ///   2) mp4v (MPEG-4 Part 2) — dosya .mp4 olur ama TARAYICI OYNATAMAZ,
    ///      sadece VLC gibi oynatıcılarda çalışır. H.264 hiç çalışmazsa buraya düşülür.
    ///   3) MJPG/AVI — son çare; bu da tarayıcıda oynamaz.
    /// </summary>
    private void DetectWorkingCodec()
    {
        var candidates = new (string Fourcc, string Extension, bool BrowserCompatible)[]
        {
            ("avc1", "mp4", true),
            ("H264", "mp4", true),
            ("mp4v", "mp4", false),
            ("MJPG", "avi", false),
        };

        foreach (var (fourcc, extension, browserCompatible) in candidates)
        {
            if (ProbeCodecReallyEncodes(fourcc, extension))
            {
                _codecFourcc = fourcc;
                _codecExtension = extension;
                _codecIsBrowserCompatible = browserCompatible;
                _codecDetected = true;

                if (browserCompatible)
                {
                    _logger.LogInformation("Codec tespiti: {Fourcc} kullanılacak (tarayıcıda oynatılabilir).", fourcc);
                }
                else
                {
                    _logger.LogWarning(
                        "Codec tespiti: tarayıcı-uyumlu H.264 encoder bulunamadı. {Fourcc} kullanılacak — " +
                        "kayıtlar diske düzgün yazılır ama web arayüzünde OYNATILAMAZ, VLC gibi bir oynatıcı gerekir.",
                        fourcc);
                }

                return;
            }
        }

        _codecFourcc = "mp4v";
        _codecExtension = "mp4";
        _codecIsBrowserCompatible = false;
        _codecDetected = true;
        _logger.LogError("Codec tespiti: hiçbir aday doğrulanamadı, mp4v'ye güvenerek devam ediliyor (test edilmemiş).");
    }

    /// <summary>
    /// Bir codec'in gerçekten çalışıp çalışmadığını "yaz, sonra GERİ OKU" ile
    /// doğrular (round-trip). Boyut karşılaştırması yanıltıcı çıkabiliyor (düz
    /// renkli test kareleri gerçek bir encoder'da bile neredeyse hiç büyümüyor),
    /// round-trip bundan etkilenmiyor: yazma sessizce başarısız olduysa geri
    /// okunacak hiçbir şey olmaz, gerçekten yazdıysa yazdığımız kadar kare geri gelir.
    /// </summary>
    private bool ProbeCodecReallyEncodes(string fourcc, string extension)
    {
        var probeSize = new OpenCvSharp.Size(320, 240);
        const int writtenFrames = 15;
        var path = Path.Combine(Path.GetTempPath(), $"endoscopy_codec_probe_{fourcc}_{Guid.NewGuid():N}.{extension}");

        try
        {
            using (var frame = new Mat(probeSize, MatType.CV_8UC3, Scalar.All(128)))
            using (var writer = new VideoWriter(path, VideoWriter.FourCC(fourcc[0], fourcc[1], fourcc[2], fourcc[3]), 20, probeSize))
            {
                if (!writer.IsOpened()) return false;
                for (int i = 0; i < writtenFrames; i++) writer.Write(frame);
            } // using bloğu bitince Dispose -> Release çağrılır, dosya finalize olur

            using var reader = new VideoCapture(path);
            if (!reader.IsOpened()) return false;

            var readableFrames = 0;
            using var readFrame = new Mat();
            while (reader.Read(readFrame) && !readFrame.Empty())
            {
                readableFrames++;
                if (readableFrames > writtenFrames + 5) break; // güvenlik: sonsuz döngü olmasın
            }

            // Yarısından fazlası geri okunabiliyorsa codec gerçekten çalışıyor say.
            return readableFrames >= writtenFrames / 2;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* geçici dosya, temizlenemezse önemsiz */ }
        }
    }

    /// <summary>
    /// Var olan bir video dosyasını (kayıt sürecinin dışında, elle) yeniden açıp
    /// gerçek genişlik/yükseklik/kare sayısını döner. "Backfill" senaryosu için:
    /// bu alanlar eklenmeden önce kaydedilmiş eski dosyaların DB'sini sonradan
    /// doldurmak. VideoCapture'ın kendi container metadata'sından (moov'daki
    /// bilgiden) okunuyor — tüm kareleri tek tek okumaya gerek yok, hızlı.
    /// Dosya açılamazsa null döner.
    /// </summary>
    public (int Width, int Height, long FrameCount)? ReadVideoFileMetadata(string path)
    {
        try
        {
            using var reader = new VideoCapture(path);
            if (!reader.IsOpened()) return null;
            return (reader.FrameWidth, reader.FrameHeight, (long)reader.FrameCount);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Web'e (MJPEG akışı için) JPEG olarak kodlanmış son kareyi döner.</summary>
    public byte[]? GetLatestFrameJpeg()
    {
        lock (_lock)
        {
            if (_latestFrame.Empty()) return null;
            Cv2.ImEncode(".jpg", _latestFrame, out var buffer);
            return buffer;
        }
    }

    /// <summary>
    /// "Capture" (kare yakalama) anında çağrılır: tıpkı seri porttan
    /// tetikleyici geldiğinde belleğin o anki kopyasının alınması gibi.
    /// </summary>
    public Mat? GetLatestFrameClone()
    {
        lock (_lock)
        {
            if (_latestFrame.Empty()) return null;
            return _latestFrame.Clone();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _running = false;
        _captureThread?.Join(1000);

        // Uygulama kapanırken devam eden bir kayıt varsa yarım/bozuk dosya
        // bırakmamak için düzgünce finalize et.
        StopRecording();

        _capture?.Release();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _videoWriter?.Dispose();
        _capture?.Dispose();
        _latestFrame.Dispose();
    }
}
