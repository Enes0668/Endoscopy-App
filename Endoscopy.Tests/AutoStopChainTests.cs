using Endoscopy.Data;
using Endoscopy.Models;
using Endoscopy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Endoscopy.Tests;

/// <summary>
/// Program.cs'deki RecordingAutoStopped event handler zincirini test eder.
///
/// Zincir şöyle işler:
///   CameraSession (bağlantı koptu/disk doldu)
///     → CameraSessionManager.RecordingAutoStopped event ateşlendi
///         → Program.cs handler: DB'de aktif kaydı bul → Interrupted yap
///
/// Program.cs top-level statements kullandığından doğrudan test edilemez.
/// Bunun yerine zincirin mantığını ayrı bir test class'ında simüle ediyoruz:
/// aynı event'i dinleyip aynı DB güncellemesini yapan bir handler yazıyor,
/// sonra handler'ın doğru çalışıp çalışmadığını doğruluyoruz.
/// Bu, Program.cs'deki kodu satır satır tekrar etmeden davranışı teyit eder.
/// </summary>
public class AutoStopChainTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static CameraSessionManager CreateManager()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);
        return new CameraSessionManager(loggerFactory.Object);
    }

    /// <summary>
    /// Program.cs'deki event handler mantığını simüle eden yardımcı:
    /// RecordingAutoStopped event'ini dinler ve DB'yi Interrupted olarak günceller.
    /// </summary>
    private static void WireAutoStopHandler(CameraSessionManager manager, AppDbContext db)
    {
        var logger = new Mock<ILogger>().Object;
        var dbService = new CaptureDbService(db, new Mock<ILogger<CaptureDbService>>().Object);

        manager.RecordingAutoStopped += info =>
        {
            var active = dbService.GetActiveRecording(info.RoomId);
            if (active == null) return;

            var endedAt    = DateTimeOffset.UtcNow;
            var durationMs = (long)(endedAt - active.CapturedAt).TotalMilliseconds;
            dbService.CompleteVideoCapture(
                active.Id, endedAt, durationMs,
                CaptureStatus.Interrupted,
                info.Width, info.Height, info.FrameCount, info.FileSizeBytes);
        };
    }

    // -----------------------------------------------------------------------
    // Testler
    // -----------------------------------------------------------------------

    [Fact]
    public void BaglantıKopunca_DBdekiKayitInterruptedOlmali()
    {
        // 1. Hazırlık: DB'de aktif bir kayıt var.
        using var db = CreateDb();
        var manager  = CreateManager();
        WireAutoStopHandler(manager, db);

        var dbService = new CaptureDbService(db, new Mock<ILogger<CaptureDbService>>().Object);
        var recordingId = dbService.InsertCapture(
            CaptureType.Video, "/storage/video.mp4",
            DateTimeOffset.UtcNow.AddMinutes(-3), "WEB_BUTTON",
            status: CaptureStatus.Recording,
            context: new CaptureContext(RoomName: "oda1"));

        // 2. Oturum oluştur ve "kayıt var + bağlantı koptu" simüle et.
        var session = manager.GetOrCreate("oda1");
        SetSessionRecording(session, true);
        SetSessionFilePath(session, "/storage/video.mp4");

        session.NotifyConnectionClosed();

        // 3. Doğrula: DB'deki kayıt Interrupted olmalı.
        var entity = db.MediaCaptures.Find(recordingId)!;
        Assert.Equal(CaptureStatus.Interrupted, entity.Status);
        Assert.NotNull(entity.EndedAt);
        Assert.True(entity.DurationMs >= 0);
    }

    [Fact]
    public void BaglantıKopunca_YanlisOdaIcinDBGuncellemesi_Yapilmamali()
    {
        // "oda1" bağlantısı koptu, ama DB'de sadece "oda2"nin aktif kaydı var.
        // "oda2"nin kaydına dokunulmamalı.
        using var db = CreateDb();
        var manager  = CreateManager();
        WireAutoStopHandler(manager, db);

        var dbService = new CaptureDbService(db, new Mock<ILogger<CaptureDbService>>().Object);
        var oda2Id = dbService.InsertCapture(
            CaptureType.Video, "/storage/oda2.mp4",
            DateTimeOffset.UtcNow.AddMinutes(-5), "WEB_BUTTON",
            status: CaptureStatus.Recording,
            context: new CaptureContext(RoomName: "oda2"));

        // oda1 için session aç ve bağlantısını kes.
        var session1 = manager.GetOrCreate("oda1");
        SetSessionRecording(session1, true);
        SetSessionFilePath(session1, "/storage/oda1.mp4");
        session1.NotifyConnectionClosed();

        // oda2'nin kaydı hâlâ Recording olmalı (etkilenmemeli).
        var oda2Entity = db.MediaCaptures.Find(oda2Id)!;
        Assert.Equal(CaptureStatus.Recording, oda2Entity.Status);
    }

    [Fact]
    public void BaglantıKopunca_DbdeAktifKayitYoksa_ExceptionFirlatmamali()
    {
        // Event handler DB'de kayıt bulamazsa sessizce devam etmeli.
        using var db = CreateDb();
        var manager  = CreateManager();
        WireAutoStopHandler(manager, db);

        var session = manager.GetOrCreate("boş-oda");
        SetSessionRecording(session, true);
        SetSessionFilePath(session, "/storage/fake.mp4");

        var exception = Record.Exception(() => session.NotifyConnectionClosed());

        Assert.Null(exception);
    }

    [Fact]
    public void BirdenFazlaOda_AyniAndaBaglantıKopunca_SadeceKendiKaydiGuncellenir()
    {
        // oda1 ve oda2 aynı anda bağlantı kesince kendi DB kayıtlarını günceller,
        // birbirini etkilemez.
        using var db = CreateDb();
        var manager  = CreateManager();
        WireAutoStopHandler(manager, db);

        var dbService = new CaptureDbService(db, new Mock<ILogger<CaptureDbService>>().Object);
        var id1 = dbService.InsertCapture(CaptureType.Video, "/s/1.mp4",
            DateTimeOffset.UtcNow.AddMinutes(-2), "BTN",
            status: CaptureStatus.Recording,
            context: new CaptureContext(RoomName: "oda1"));
        var id2 = dbService.InsertCapture(CaptureType.Video, "/s/2.mp4",
            DateTimeOffset.UtcNow.AddMinutes(-2), "BTN",
            status: CaptureStatus.Recording,
            context: new CaptureContext(RoomName: "oda2"));

        var s1 = manager.GetOrCreate("oda1");
        var s2 = manager.GetOrCreate("oda2");
        SetSessionRecording(s1, true); SetSessionFilePath(s1, "/s/1.mp4");
        SetSessionRecording(s2, true); SetSessionFilePath(s2, "/s/2.mp4");

        s1.NotifyConnectionClosed();
        s2.NotifyConnectionClosed();

        Assert.Equal(CaptureStatus.Interrupted, db.MediaCaptures.Find(id1)!.Status);
        Assert.Equal(CaptureStatus.Interrupted, db.MediaCaptures.Find(id2)!.Status);
    }

    // -----------------------------------------------------------------------
    // Reflection yardımcıları
    // -----------------------------------------------------------------------

    private static void SetSessionRecording(CameraSession session, bool value)
    {
        typeof(CameraSession)
            .GetField("_isRecording", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(session, value);
    }

    private static void SetSessionFilePath(CameraSession session, string path)
    {
        typeof(CameraSession)
            .GetField("_currentFilePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(session, path);
    }
}
