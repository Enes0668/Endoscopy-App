using OpenCvSharp;

namespace Endoscopy.Services;

/// <summary>"Stop" isteğiyle (kullanıcı tetiklemesiyle) düzgün durdurulan kaydın sonucu.</summary>
public record VideoStopResult(string FilePath, bool IsPlaybackVerified, int Width, int Height, long FrameCount, long FileSizeBytes);

/// <summary>
/// Kaydın kullanıcı isteği OLMADAN (bağlantı koptu, disk doldu vb.) otomatik
/// durdurulduğunu bildirir. Hangi ODADA olduğu (RoomId) da taşınıyor — artık
/// tek bir kamera değil, N tane bağımsız oda olduğu için, hangi odanın kaydının
/// durduğunu bilmeden DB'yi güncelleyemeyiz.
/// </summary>
public record VideoAutoStopInfo(string RoomId, string FilePath, bool IsPlaybackVerified, string Reason, int Width, int Height, long FrameCount, long FileSizeBytes);

/// <summary>
/// Bir odanın/istasyonun kamera oturumu: en son kare ve (varsa) devam eden kaydın
/// durumu. Eski CameraService'in yerini alıyor — ama CameraService'ten farklı olarak
/// burada kendi fiziksel kamerayı açan bir arka plan thread'i (CaptureLoop) YOK.
/// Kareler, bu odanın WebSocket bağlantısının receive döngüsünden "push" olarak
/// UpdateFrame() ile geliyor — bu yüzden ayrı bir thread'e hiç gerek kalmadı.
///
/// Thread-safety kuralları CameraService'teki ile BİREBİR AYNI (bkz. bugünkü
/// konuşma): _lock sadece _latestFrame'i korur, _writerLock kayıtla ilgili her
/// şeyi korur, ikisi ASLA iç içe (nested) alınmaz — CaptureService'te bulduğumuz
/// deadlock kuralı burada da geçerli. Fark: artık N tane CameraSession örneği var
/// (her oda kendi _lock/_writerLock'una sahip), bu yüzden bir odanın kilitleri
/// başka bir odayı hiç etkilemiyor — odalar birbirinden tamamen izole.
/// </summary>
public class CameraSession
{
    // Kayıt sürerken bu aralıkla (her karede değil) diskteki boş alanı kontrol ediyoruz.
    private static readonly TimeSpan DiskSpaceCheckInterval = TimeSpan.FromSeconds(5);

    // Bu eşiğin altına inen boş disk alanında ne yeni kayıt başlatılır ne de
    // devam eden kayıt sürdürülür.
    private const long MinFreeBytesForRecording = 500L * 1024 * 1024;

    // Bu süreden uzun süredir kare gelmemişse, oda "canlı değil" sayılır
    // (tarayıcı sekmesi kapanmış, ağ kopmuş vb. olabilir).
    private static readonly TimeSpan LiveThreshold = TimeSpan.FromSeconds(5);

    private readonly string _roomId;
    private readonly ILogger _logger;
    private readonly Action<VideoAutoStopInfo> _onAutoStop;

    private readonly object _lock = new();
    private readonly Mat _latestFrame = new();

    // _writerLock hiçbir zaman _lock ile iç içe (nested) alınmaz — CameraService'teki
    // aynı kural, aynı gerekçeyle (deadlock'tan kaçınmak için).
    private readonly object _writerLock = new();
    private VideoWriter? _videoWriter;
    private volatile bool _isRecording;
    private string? _currentFilePath;
    private int _recordingWidth;
    private int _recordingHeight;
    private long _writtenFrameCount;
    private DateTimeOffset _lastDiskCheckAt = DateTimeOffset.MinValue;

    public string? LastStartFailureReason { get; private set; }

    /// <summary>Bu odadan en son ne zaman bir kare geldi.</summary>
    public DateTimeOffset LastFrameAt { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>Son birkaç saniye içinde kare geldi mi (tarayıcı hâlâ bağlı ve gönderiyor mu)?</summary>
    public bool IsLive => DateTimeOffset.UtcNow - LastFrameAt < LiveThreshold;

    public bool IsRecording => _isRecording;

    public CameraSession(string roomId, ILogger logger, Action<VideoAutoStopInfo> onAutoStop)
    {
        _roomId = roomId;
        _logger = logger;
        _onAutoStop = onAutoStop;
    }

    /// <summary>
    /// WebSocket'ten yeni bir kare geldiğinde çağrılır: en son kareyi günceller,
    /// kayıt açıksa aynı kareyi dosyaya da yazar. Eski CameraService.CaptureLoop
    /// ile aynı mantık, sadece bir "while" döngüsünde değil, her mesaj geldiğinde
    /// bir kere çalışıyor (push-based).
    /// </summary>
    public void UpdateFrame(Mat frame)
    {
        lock (_lock)
        {
            frame.CopyTo(_latestFrame);
        }

        LastFrameAt = DateTimeOffset.UtcNow;

        var shouldCheckDiskSpace = false;
        if (_isRecording)
        {
            lock (_writerLock)
            {
                if (_isRecording)
                {
                    _videoWriter?.Write(frame);
                    _writtenFrameCount++;

                    if (DateTimeOffset.UtcNow - _lastDiskCheckAt >= DiskSpaceCheckInterval)
                    {
                        shouldCheckDiskSpace = true;
                    }
                }
            }
        }

        if (shouldCheckDiskSpace)
        {
            _lastDiskCheckAt = DateTimeOffset.UtcNow;
            var dir = _currentFilePath != null ? Path.GetDirectoryName(_currentFilePath) : null;

            if (dir != null && GetAvailableFreeBytes(dir) < MinFreeBytesForRecording)
            {
                AutoStopRecording("disk alanı kritik seviyenin altına düştü");
            }
        }
    }

    /// <summary>
    /// WebSocket bağlantısı ne sebeple olursa olsun kapandığında çağrılmalı.
    /// Kayıt açıksa, eski CameraService'teki "kamera koptu" senaryosunun karşılığı:
    /// tarayıcı artık görüntü göndermiyor demektir, kaydı otomatik durduruyoruz.
    /// </summary>
    public void NotifyConnectionClosed()
    {
        if (_isRecording)
        {
            AutoStopRecording("bağlantı koptu (tarayıcı görüntü göndermeyi durdurdu)");
        }
    }

    public string? StartRecording(string storageDir, string baseFileName, (string Fourcc, string Extension, bool BrowserCompatible) codec, int fps = 20)
    {
        LastStartFailureReason = null;

        int width, height;
        lock (_lock)
        {
            if (_latestFrame.Empty())
            {
                LastStartFailureReason = "Bu odadan henüz bir görüntü karesi alınamadı.";
                return null;
            }
            width = _latestFrame.Width;
            height = _latestFrame.Height;
        }

        if (GetAvailableFreeBytes(storageDir) < MinFreeBytesForRecording)
        {
            LastStartFailureReason = $"Disk alanı yetersiz (en az {MinFreeBytesForRecording / (1024 * 1024)} MB boş alan gerekiyor).";
            _logger.LogError("[{RoomId}] Video kaydı başlatılamadı: {Reason}", _roomId, LastStartFailureReason);
            return null;
        }

        lock (_writerLock)
        {
            if (_isRecording)
            {
                LastStartFailureReason = "Bu odada zaten devam eden bir kayıt var.";
                return null;
            }

            var size = new OpenCvSharp.Size(width, height);
            var path = Path.Combine(storageDir, $"{baseFileName}.{codec.Extension}");
            var writer = new VideoWriter(path, VideoWriter.FourCC(codec.Fourcc[0], codec.Fourcc[1], codec.Fourcc[2], codec.Fourcc[3]), fps, size);

            if (!writer.IsOpened())
            {
                writer.Dispose();
                LastStartFailureReason = $"Önceden doğrulanmış codec ({codec.Fourcc}) bu sefer açılamadı.";
                _logger.LogError("[{RoomId}] Video kaydı başlatılamadı: {Reason} Path={Path}", _roomId, LastStartFailureReason, path);
                return null;
            }

            _videoWriter = writer;
            _currentFilePath = path;
            _recordingWidth = width;
            _recordingHeight = height;
            _writtenFrameCount = 0;
            _isRecording = true;

            _logger.LogInformation(
                "[{RoomId}] Video kaydı başladı: {Path} ({Width}x{Height}, {Fps}fps, codec={Fourcc})",
                _roomId, path, width, height, fps, codec.Fourcc);

            return path;
        }
    }

    public VideoStopResult? StopRecording()
    {
        var closed = CloseCurrentRecordingFile();
        if (closed == null) return null;

        var (filePath, verified, width, height, frameCount, fileSizeBytes) = closed.Value;
        _logger.LogInformation(
            "[{RoomId}] Video kaydı durduruldu: {Path} ({Width}x{Height}, {FrameCount} kare, {FileSizeBytes} byte, oynatma doğrulaması: {Verified})",
            _roomId, filePath, width, height, frameCount, fileSizeBytes, verified ? "başarılı" : "BAŞARISIZ");

        return new VideoStopResult(filePath, verified, width, height, frameCount, fileSizeBytes);
    }

    private void AutoStopRecording(string reason)
    {
        var closed = CloseCurrentRecordingFile();
        if (closed == null) return;

        var (filePath, verified, width, height, frameCount, fileSizeBytes) = closed.Value;
        _logger.LogError(
            "[{RoomId}] Video kaydı otomatik durduruldu ({Reason}): {Path} ({Width}x{Height}, {FrameCount} kare, {FileSizeBytes} byte, oynatma doğrulaması: {Verified})",
            _roomId, reason, filePath, width, height, frameCount, fileSizeBytes, verified ? "başarılı" : "BAŞARISIZ");

        _onAutoStop(new VideoAutoStopInfo(_roomId, filePath, verified, reason, width, height, frameCount, fileSizeBytes));
    }

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

    private long GetAvailableFreeBytes(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return long.MaxValue;

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
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

    /// <summary>"Capture" (kare yakalama) anında çağrılır.</summary>
    public Mat? GetLatestFrameClone()
    {
        lock (_lock)
        {
            if (_latestFrame.Empty()) return null;
            return _latestFrame.Clone();
        }
    }
}
