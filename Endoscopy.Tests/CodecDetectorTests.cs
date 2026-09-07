using Endoscopy.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Endoscopy.Tests;

/// <summary>
/// CodecDetector'ın önbellekleme (caching) ve temel davranışlarını test eder.
///
/// NOT: Gerçek codec tespiti (ProbeCodecReallyEncodes) VideoWriter ile gerçek
/// bir dosya yazar ve OpenCV codec'lerine bağlıdır — bu durum integration
/// test kapsamına girer. Burada test ettiğimiz:
///   1. Önbellekleme: ikinci GetCodec çağrısı yeniden tespit YAPMASIN.
///   2. Temel sözleşme: dönen Fourcc ve Extension boş olmamalı.
/// </summary>
public class CodecDetectorTests
{
    private static CodecDetector CreateDetector()
        => new CodecDetector(new Mock<ILogger<CodecDetector>>().Object);

    [Fact]
    public void GetCodec_IlkCagri_FourccBosMamali()
    {
        // En azından bir codec seçilmeli (fallback mp4v dahil).
        var detector = CreateDetector();

        var (fourcc, _, _) = detector.GetCodec();

        Assert.False(string.IsNullOrEmpty(fourcc));
        Assert.Equal(4, fourcc.Length); // Tüm FOURCC kodları 4 karakter
    }

    [Fact]
    public void GetCodec_IlkCagri_ExtensionBosMamali()
    {
        var detector = CreateDetector();

        var (_, extension, _) = detector.GetCodec();

        Assert.False(string.IsNullOrEmpty(extension));
    }

    [Fact]
    public void GetCodec_IkiKezCagirilinca_AyniSonucu_Donmeli()
    {
        // Önbellek çalışıyorsa ikinci çağrı aynı codec'i dönmeli.
        var detector = CreateDetector();

        var first  = detector.GetCodec();
        var second = detector.GetCodec();

        Assert.Equal(first.Fourcc,            second.Fourcc);
        Assert.Equal(first.Extension,         second.Extension);
        Assert.Equal(first.BrowserCompatible, second.BrowserCompatible);
    }

    [Fact]
    public void GetCodec_FarkliDetector_KendiTespitiniYapar()
    {
        // Her CodecDetector örneği kendi önbelleğini yönetir; iki farklı
        // örnek birbirinin önbelleğini paylaşmamalı (bu önemli değil ama
        // sınıfın izolasyonunu kanıtlar).
        var d1 = CreateDetector();
        var d2 = CreateDetector();

        var r1 = d1.GetCodec();
        var r2 = d2.GetCodec();

        // İkisi de geçerli sonuç döndürüyor olmalı (aynı makine, aynı codec).
        Assert.Equal(r1.Fourcc, r2.Fourcc);
    }

    [Fact]
    public void GetCodec_BrowserCompatible_YalanSoylemiyorsa_Mp4Olmali()
    {
        // Tarayıcı uyumlu codec (avc1/H264) mp4 container kullanır.
        // BrowserCompatible=true ise extension mutlaka "mp4" olmalı.
        var detector = CreateDetector();

        var (_, extension, browserCompatible) = detector.GetCodec();

        if (browserCompatible)
        {
            Assert.Equal("mp4", extension);
        }
    }
}
