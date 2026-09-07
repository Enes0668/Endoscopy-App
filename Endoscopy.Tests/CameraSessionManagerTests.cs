using Endoscopy.Services;
using Microsoft.Extensions.Logging;
using Moq;
using OpenCvSharp;

namespace Endoscopy.Tests;

/// <summary>
/// CameraSessionManager sınıfının davranışlarını test eder.
/// Manager, odaların CameraSession'larını tutan bir kayıt defteridir;
/// buradaki testler doğru session üretilip üretilmediğini ve thread-safe
/// erişimin doğruluğunu doğrular.
/// </summary>
public class CameraSessionManagerTests
{
    // -----------------------------------------------------------------------
    // Yardımcı fabrika
    // -----------------------------------------------------------------------

    private static CameraSessionManager CreateManager()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);
        return new CameraSessionManager(loggerFactory.Object);
    }

    // -----------------------------------------------------------------------
    // GetOrCreate
    // -----------------------------------------------------------------------

    [Fact]
    public void GetOrCreate_AyniRoomId_HerCagridaAyniSesyonuDonmeli()
    {
        // Aynı roomId ile iki kez çağrıldığında aynı nesne dönmelidir
        // (ConcurrentDictionary'nin GetOrAdd garantisi).
        var manager = CreateManager();

        var session1 = manager.GetOrCreate("oda1");
        var session2 = manager.GetOrCreate("oda1");

        Assert.Same(session1, session2);
    }

    [Fact]
    public void GetOrCreate_FarkliRoomIdler_FarkliSesyonlarOlusturmali()
    {
        // Farklı roomId'ler birbirinden bağımsız session almalı.
        var manager = CreateManager();

        var session1 = manager.GetOrCreate("oda1");
        var session2 = manager.GetOrCreate("oda2");

        Assert.NotSame(session1, session2);
    }

    // -----------------------------------------------------------------------
    // TryGet
    // -----------------------------------------------------------------------

    [Fact]
    public void TryGet_VarOlmayanOda_NullDonmeli()
    {
        // Henüz hiç bağlanmamış odada TryGet null dönmeli, yeni session OLUŞTURMAMALI.
        var manager = CreateManager();

        var result = manager.TryGet("olmayan-oda");

        Assert.Null(result);
    }

    [Fact]
    public void TryGet_GetOrCreateSonrasinda_SesyonuBulabilmeli()
    {
        // GetOrCreate ile oluşturulan session, TryGet ile bulunabilmeli.
        var manager = CreateManager();
        var created = manager.GetOrCreate("oda1");

        var found = manager.TryGet("oda1");

        Assert.NotNull(found);
        Assert.Same(created, found);
    }

    [Fact]
    public void TryGet_VarOlmayanOda_YeniSesyonOlusturulmamali()
    {
        // TryGet çağrısı dictionary'ye yeni giriş EKLEMEMELİ.
        var manager = CreateManager();

        manager.TryGet("hayalet-oda");

        var knownRooms = manager.GetKnownRoomIds();
        Assert.DoesNotContain("hayalet-oda", knownRooms);
    }

    // -----------------------------------------------------------------------
    // GetKnownRoomIds
    // -----------------------------------------------------------------------

    [Fact]
    public void GetKnownRoomIds_BaslangicDurumunda_BosOlmali()
    {
        var manager = CreateManager();

        var ids = manager.GetKnownRoomIds();

        Assert.Empty(ids);
    }

    [Fact]
    public void GetKnownRoomIds_OdaEklendikten_Sonra_OdayiIcermeli()
    {
        var manager = CreateManager();
        manager.GetOrCreate("oda-yeni");

        var ids = manager.GetKnownRoomIds();

        Assert.Contains("oda-yeni", ids);
    }

    [Fact]
    public void GetKnownRoomIds_BirdenFazlaOda_HepsiniIcermeli()
    {
        var manager = CreateManager();
        manager.GetOrCreate("oda-a");
        manager.GetOrCreate("oda-b");
        manager.GetOrCreate("oda-c");

        var ids = manager.GetKnownRoomIds();

        Assert.Equal(3, ids.Count);
        Assert.Contains("oda-a", ids);
        Assert.Contains("oda-b", ids);
        Assert.Contains("oda-c", ids);
    }

    // -----------------------------------------------------------------------
    // RecordingAutoStopped event
    // -----------------------------------------------------------------------

    [Fact]
    public void RecordingAutoStopped_KayitKopunca_EventTetiklenmeli()
    {
        // Session'da bağlantı koptuğunda, manager'ın RecordingAutoStopped
        // event'i tetiklenmelidir. Bu, manager → event → Program.cs DB güncelleme
        // zincirinin ilk halkasını doğrular.
        var manager = CreateManager();
        VideoAutoStopInfo? received = null;
        manager.RecordingAutoStopped += info => received = info;

        var session = manager.GetOrCreate("oda-event-test");

        // Kayıt açık gibi gösterip bağlantıyı kesiyoruz (reflection ile).
        var isRecField = typeof(CameraSession).GetField(
            "_isRecording",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        isRecField.SetValue(session, true);

        var filePathField = typeof(CameraSession).GetField(
            "_currentFilePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        filePathField.SetValue(session, Path.Combine(Path.GetTempPath(), "event_test.mp4"));

        session.NotifyConnectionClosed();

        Assert.NotNull(received);
        Assert.Equal("oda-event-test", received!.RoomId);
    }
}
