using Endoscopy.Services;
using Microsoft.Extensions.Logging;
using Moq;
using OpenCvSharp;

namespace Endoscopy.Tests;

/// <summary>
/// FileIdentityTagger'ın davranışlarını test eder.
///
/// Tagger gerçek dosyalara yazar (TagLibSharp ile), bu yüzden testlerde geçici
/// klasörde gerçek dosyalar oluşturuyoruz ve her test sonunda temizliyoruz.
/// Bu durum "unit" sınırını biraz zorluyor, ama alternatif (TagLib.File'ı
/// interface arkasına almak) büyük refactor gerektirirdi — mevcut fayda/maliyet
/// oranında bu haliyle daha pratik.
/// </summary>
public class FileIdentityTaggerTests : IDisposable
{
    private readonly string _testDir;
    private readonly ILogger _logger = new Mock<ILogger>().Object;

    public FileIdentityTaggerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"endoscopy_tagger_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* geçici klasör, temizlenemezse önemsiz */ }
    }

    // -----------------------------------------------------------------------
    // JPEG testleri
    // -----------------------------------------------------------------------

    [Fact]
    public void TryWriteCaptureIdentity_GecerliJpeg_TrueVeyaFalseDoner_ExceptionFirlatmaz()
    {
        // Gerçek bir JPEG oluştur.
        var path = Path.Combine(_testDir, "test.jpg");
        using var frame = new Mat(new Size(320, 240), MatType.CV_8UC3, Scalar.All(128));
        Cv2.ImWrite(path, frame);

        // Exception fırlatmamalı; dönen değer true/false olabilir (format desteğine bağlı).
        var exception = Record.Exception(() =>
            FileIdentityTagger.TryWriteCaptureIdentity(
                path, "TEST-MAKINE", "192.168.1.1", "AA:BB:CC:DD:EE:FF", _logger));

        Assert.Null(exception);
    }

    [Fact]
    public void TryWriteCaptureIdentity_OlmayanDosya_FalseDoner_ExceptionFirlatmaz()
    {
        // Var olmayan dosyaya yazma denemesi exception fırlatmamalı, false dönmeli.
        var path = Path.Combine(_testDir, "olmayan.jpg");

        var result = FileIdentityTagger.TryWriteCaptureIdentity(
            path, "TEST-MAKINE", null, null, _logger);

        Assert.False(result);
    }

    [Fact]
    public void TryWriteCaptureIdentity_NullIpVeMac_ExceptionFirlatmaz()
    {
        // IP ve MAC null geçilince exception çıkmamalı (opsiyonel alanlar).
        var path = Path.Combine(_testDir, "test_null.jpg");
        using var frame = new Mat(new Size(320, 240), MatType.CV_8UC3, Scalar.All(64));
        Cv2.ImWrite(path, frame);

        var exception = Record.Exception(() =>
            FileIdentityTagger.TryWriteCaptureIdentity(
                path, "MAKINE", ipAddress: null, macAddress: null, _logger));

        Assert.Null(exception);
    }

    // -----------------------------------------------------------------------
    // Desteklenmeyen format
    // -----------------------------------------------------------------------

    [Fact]
    public void TryWriteCaptureIdentity_DesteklenimeyenFormat_FalseDoner_ExceptionFirlatmaz()
    {
        // .txt dosyası: TagLib ne XMP ne Apple tag destekler → false dönmeli.
        var path = Path.Combine(_testDir, "metin.txt");
        File.WriteAllText(path, "Bu bir test dosyasıdır.");

        var result = FileIdentityTagger.TryWriteCaptureIdentity(
            path, "MAKINE", "10.0.0.1", "FF:EE:DD:CC:BB:AA", _logger);

        // TagLibSharp bu formatı okuyamayabilir (exception kaldırılmış) ya da
        // "hiçbir tag yazmadım" diye false döner — ikisi de geçerli.
        // Önemli olan: exception FIRLAMAMASI.
        Assert.False(result); // txt için her iki durumda da false bekliyoruz
    }
}
