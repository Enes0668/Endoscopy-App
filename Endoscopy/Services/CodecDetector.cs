using OpenCvSharp;

namespace Endoscopy.Services;

/// <summary>
/// Bu makinede video kaydı için gerçekten çalışan codec'i tespit edip önbelleğe
/// alan, tüm oda/kamera oturumlarının PAYLAŞTIĞI tek bir singleton. Codec uyumluluğu
/// bir kameranın değil, bu SUNUCU makinesinin (üzerindeki encoder DLL'lerinin)
/// özelliği olduğu için oda başına ayrı ayrı tespit etmeye gerek yok — bir kere
/// tespit edilir, herkes aynı sonucu kullanır.
///
/// Eski CameraService.DetectWorkingCodec ile birebir aynı mantık (yaz-sonra-geri-oku
/// doğrulaması). Tek fark: artık "kayıt başlatma" tek bir yerde değil, N farklı
/// oda/oturumda aynı anda olabileceği için, check-then-act (_detected kontrolü)
/// kendi kilidi (_lock) İÇİNDE yapılıyor — sabah CameraService'te bulup düzelttiğimiz
/// "iki istemci aynı anda tespiti tetikler" race'ine burada baştan düşmemek için.
/// </summary>
public class CodecDetector
{
    private readonly ILogger<CodecDetector> _logger;
    private readonly object _lock = new();

    private bool _detected;
    private string _fourcc = "mp4v";
    private string _extension = "mp4";
    private bool _browserCompatible;

    public CodecDetector(ILogger<CodecDetector> logger)
    {
        _logger = logger;
    }

    public (string Fourcc, string Extension, bool BrowserCompatible) GetCodec()
    {
        lock (_lock)
        {
            if (!_detected)
            {
                Detect();
            }

            return (_fourcc, _extension, _browserCompatible);
        }
    }

    private void Detect()
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
                _fourcc = fourcc;
                _extension = extension;
                _browserCompatible = browserCompatible;
                _detected = true;

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

        _fourcc = "mp4v";
        _extension = "mp4";
        _browserCompatible = false;
        _detected = true;
        _logger.LogError("Codec tespiti: hiçbir aday doğrulanamadı, mp4v'ye güvenerek devam ediliyor (test edilmemiş).");
    }

    /// <summary>
    /// Bir codec'in gerçekten çalışıp çalışmadığını "yaz, sonra GERİ OKU" ile
    /// doğrular (round-trip) — sadece IsOpened()==true'ya güvenmek yeterli değil,
    /// bazı makinelerde encoder DLL'i eksik/uyumsuz olduğunda Write() sessizce
    /// hiçbir şey yazmayabiliyor.
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
            }

            using var reader = new VideoCapture(path);
            if (!reader.IsOpened()) return false;

            var readableFrames = 0;
            using var readFrame = new Mat();
            while (reader.Read(readFrame) && !readFrame.Empty())
            {
                readableFrames++;
                if (readableFrames > writtenFrames + 5) break;
            }

            return readableFrames >= writtenFrames / 2;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* geçici dosya, temizlenemezse önemsiz */ }
        }
    }
}
