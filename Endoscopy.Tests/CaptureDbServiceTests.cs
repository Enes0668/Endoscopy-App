using Endoscopy.Data;
using Endoscopy.Models;
using Endoscopy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Endoscopy.Tests;

/// <summary>
/// CaptureDbService'in veri erişim mantığını test eder.
///
/// Her test kendi izole InMemory veritabanını alır (benzersiz DB adı sayesinde).
/// Bu sayede testler sırasında birbirini etkilemez ve paralel çalışabilir.
/// InMemory DB gerçek PostgreSQL davranışını tam taklit etmez (transaction,
/// kısıt kontrolü yok), ama servis katmanının LINQ/EF Core mantığını test
/// etmek için yeterlidir.
/// </summary>
public class CaptureDbServiceTests
{
    // -----------------------------------------------------------------------
    // Yardımcı fabrikalar
    // -----------------------------------------------------------------------

    /// <summary>
    /// Her test çağrısında taze, izole bir InMemory AppDbContext döner.
    /// Benzersiz DB adı için Guid kullanılır.
    /// </summary>
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static CaptureDbService CreateService(AppDbContext db)
    {
        var logger = new Mock<ILogger<CaptureDbService>>().Object;
        return new CaptureDbService(db, logger);
    }

    // -----------------------------------------------------------------------
    // InsertCapture
    // -----------------------------------------------------------------------

    [Fact]
    public void InsertCapture_YeniKayitEkler_SifirdenBuyukIdDonmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);

        var id = svc.InsertCapture(
            CaptureType.Photo, "/storage/foto.jpg",
            DateTimeOffset.UtcNow, "WEB_BUTTON");

        Assert.True(id > 0);
    }

    [Fact]
    public void InsertCapture_ContextBilgileri_DogruKaydedilmeli()
    {
        // PatientName, DoctorName, RoomName gibi context alanları
        // eksiksiz DB'ye yazılmalı.
        using var db = CreateDb();
        var svc = CreateService(db);
        var context = new CaptureContext(
            PatientIdentifier: "PT-001",
            PatientName: "Ayşe Yılmaz",
            DoctorName: "Dr. Mehmet",
            ProcedureType: "Kolonoskopi",
            RoomName: "oda1");

        var id = svc.InsertCapture(
            CaptureType.Photo, "/storage/foto.jpg",
            DateTimeOffset.UtcNow, "WEB_BUTTON", context: context);

        var saved = db.MediaCaptures.Find(id)!;
        Assert.Equal("Ayşe Yılmaz", saved.PatientName);
        Assert.Equal("Dr. Mehmet", saved.DoctorName);
        Assert.Equal("Kolonoskopi", saved.ProcedureType);
        Assert.Equal("oda1", saved.RoomName);
        Assert.Equal("PT-001", saved.PatientIdentifier);
    }

    [Fact]
    public void InsertCapture_VideoKaydi_StatusRecordingOlmali()
    {
        using var db = CreateDb();
        var svc = CreateService(db);

        var id = svc.InsertCapture(
            CaptureType.Video, "/storage/video.mp4",
            DateTimeOffset.UtcNow, "WEB_BUTTON",
            status: CaptureStatus.Recording);

        var saved = db.MediaCaptures.Find(id)!;
        Assert.Equal(CaptureStatus.Recording, saved.Status);
    }

    [Fact]
    public void InsertCapture_CihazBilgileri_DogruKaydedilmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);

        var id = svc.InsertCapture(
            CaptureType.Photo, "/storage/foto.jpg",
            DateTimeOffset.UtcNow, "WEB_BUTTON",
            machineName: "SUNUCU-01",
            localIpAddress: "192.168.1.10",
            localMacAddress: "AA:BB:CC:DD:EE:FF");

        var saved = db.MediaCaptures.Find(id)!;
        Assert.Equal("SUNUCU-01", saved.MachineName);
        Assert.Equal("192.168.1.10", saved.LocalIpAddress);
        Assert.Equal("AA:BB:CC:DD:EE:FF", saved.LocalMacAddress);
    }

    // -----------------------------------------------------------------------
    // CompleteVideoCapture
    // -----------------------------------------------------------------------

    [Fact]
    public void CompleteVideoCapture_MevcutKayit_StatusVeSureyiGuncellenmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var id = svc.InsertCapture(CaptureType.Video, "/storage/v.mp4",
            startedAt, "WEB_BUTTON", status: CaptureStatus.Recording);

        var endedAt = DateTimeOffset.UtcNow;
        svc.CompleteVideoCapture(id, endedAt, 300_000,
            CaptureStatus.Completed, width: 1920, height: 1080,
            frameCount: 1500, fileSizeBytes: 50_000_000);

        var saved = db.MediaCaptures.Find(id)!;
        Assert.Equal(CaptureStatus.Completed, saved.Status);
        Assert.Equal(endedAt, saved.EndedAt);
        Assert.Equal(300_000, saved.DurationMs);
        Assert.Equal(1920, saved.Width);
        Assert.Equal(1080, saved.Height);
        Assert.Equal(1500, saved.FrameCount);
        Assert.Equal(50_000_000, saved.FileSizeBytes);
    }

    [Fact]
    public void CompleteVideoCapture_YanlisId_HataAtmamali()
    {
        // Var olmayan ID geçilirse exception fırlatılmamalı, sadece log yazılmalı.
        using var db = CreateDb();
        var svc = CreateService(db);

        var exception = Record.Exception(() =>
            svc.CompleteVideoCapture(9999, DateTimeOffset.UtcNow, 1000));

        Assert.Null(exception);
    }

    // -----------------------------------------------------------------------
    // GetById
    // -----------------------------------------------------------------------

    [Fact]
    public void GetById_MevcutId_KaydiDonmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        var id = svc.InsertCapture(CaptureType.Photo, "/storage/p.jpg",
            DateTimeOffset.UtcNow, "WEB_BUTTON");

        var result = svc.GetById(id);

        Assert.NotNull(result);
        Assert.Equal(id, result!.Id);
    }

    [Fact]
    public void GetById_YanlisId_NullDonmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);

        var result = svc.GetById(99999);

        Assert.Null(result);
    }

    // -----------------------------------------------------------------------
    // GetCaptures — listeleme ve filtreleme
    // -----------------------------------------------------------------------

    [Fact]
    public void GetCaptures_RoomdIdNull_TumOdalariDonmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        svc.InsertCapture(CaptureType.Photo, "/s/a.jpg", DateTimeOffset.UtcNow, "BTN",
            context: new CaptureContext(RoomName: "oda1"));
        svc.InsertCapture(CaptureType.Photo, "/s/b.jpg", DateTimeOffset.UtcNow, "BTN",
            context: new CaptureContext(RoomName: "oda2"));

        var result = svc.GetCaptures(roomName: null);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetCaptures_RoomIdFiltresi_SadeceOOdayiDonmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        svc.InsertCapture(CaptureType.Photo, "/s/a.jpg", DateTimeOffset.UtcNow, "BTN",
            context: new CaptureContext(RoomName: "oda1"));
        svc.InsertCapture(CaptureType.Photo, "/s/b.jpg", DateTimeOffset.UtcNow, "BTN",
            context: new CaptureContext(RoomName: "oda2"));

        var result = svc.GetCaptures(roomName: "oda1");

        Assert.Single(result);
        Assert.Equal("oda1", result[0].RoomName);
    }

    [Fact]
    public void GetCaptures_AltKameraPrefix_AnaTodaKayitlarindaGorunmeli()
    {
        // "oda1-cam1" ve "oda1-cam2" kayıtları "oda1" sorgusuyla görünmeli.
        // Çok kameralı oda desteğinin DB katmanındaki doğrulaması.
        using var db = CreateDb();
        var svc = CreateService(db);
        svc.InsertCapture(CaptureType.Video, "/s/c1.mp4", DateTimeOffset.UtcNow, "BTN",
            context: new CaptureContext(RoomName: "oda1-cam1"));
        svc.InsertCapture(CaptureType.Video, "/s/c2.mp4", DateTimeOffset.UtcNow, "BTN",
            context: new CaptureContext(RoomName: "oda1-cam2"));
        svc.InsertCapture(CaptureType.Photo, "/s/x.jpg", DateTimeOffset.UtcNow, "BTN",
            context: new CaptureContext(RoomName: "oda2"));

        var result = svc.GetCaptures(roomName: "oda1");

        // Sadece oda1-cam1 ve oda1-cam2 görünmeli, oda2 görünmemeli.
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.StartsWith("oda1", r.RoomName));
    }

    [Fact]
    public void GetCaptures_SoftDeleteliKayit_Gizlenmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        var id = svc.InsertCapture(CaptureType.Photo, "/s/gizli.jpg",
            DateTimeOffset.UtcNow, "BTN");

        svc.DeactivateCapture(id);

        var result = svc.GetCaptures();
        Assert.DoesNotContain(result, r => r.Id == id);
    }

    [Fact]
    public void GetCaptures_EnYeniden_EskiyeSiralanmali()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        var now = DateTimeOffset.UtcNow;
        svc.InsertCapture(CaptureType.Photo, "/s/eski.jpg", now.AddMinutes(-10), "BTN");
        svc.InsertCapture(CaptureType.Photo, "/s/yeni.jpg", now, "BTN");

        var result = svc.GetCaptures();

        // İlk eleman en yeni olmalı.
        Assert.True(result[0].CapturedAt >= result[1].CapturedAt);
    }

    // -----------------------------------------------------------------------
    // DeactivateCapture — soft-delete
    // -----------------------------------------------------------------------

    [Fact]
    public void DeactivateCapture_MevcutId_IsActiveFalseYapmali()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        var id = svc.InsertCapture(CaptureType.Photo, "/s/foto.jpg",
            DateTimeOffset.UtcNow, "BTN");

        var result = svc.DeactivateCapture(id);

        Assert.True(result);
        var entity = db.MediaCaptures.Find(id)!;
        Assert.False(entity.IsActive);
    }

    [Fact]
    public void DeactivateCapture_YanlisId_FalseDonmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);

        var result = svc.DeactivateCapture(88888);

        Assert.False(result);
    }

    // -----------------------------------------------------------------------
    // GetActiveRecording
    // -----------------------------------------------------------------------

    [Fact]
    public void GetActiveRecording_RecordingStatuslu_KaydiDonmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        svc.InsertCapture(CaptureType.Video, "/s/aktif.mp4",
            DateTimeOffset.UtcNow, "BTN", status: CaptureStatus.Recording,
            context: new CaptureContext(RoomName: "oda1"));

        var result = svc.GetActiveRecording("oda1");

        Assert.NotNull(result);
        Assert.Equal(CaptureStatus.Recording, result!.Status);
    }

    [Fact]
    public void GetActiveRecording_CompletedKayit_NullDonmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        svc.InsertCapture(CaptureType.Video, "/s/bitti.mp4",
            DateTimeOffset.UtcNow, "BTN", status: CaptureStatus.Completed);

        var result = svc.GetActiveRecording();

        Assert.Null(result);
    }

    [Fact]
    public void GetActiveRecording_RoomIdFiltresi_SadeceOOdaniKaydiniBulmali()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        svc.InsertCapture(CaptureType.Video, "/s/oda1.mp4",
            DateTimeOffset.UtcNow, "BTN", status: CaptureStatus.Recording,
            context: new CaptureContext(RoomName: "oda1"));
        svc.InsertCapture(CaptureType.Video, "/s/oda2.mp4",
            DateTimeOffset.UtcNow, "BTN", status: CaptureStatus.Recording,
            context: new CaptureContext(RoomName: "oda2"));

        var result = svc.GetActiveRecording("oda2");

        Assert.NotNull(result);
        Assert.Equal("oda2", result!.RoomName);
    }

    // -----------------------------------------------------------------------
    // ReconcileStaleRecordings
    // -----------------------------------------------------------------------

    [Fact]
    public void ReconcileStaleRecordings_RecordingStatuslu_InterruptedYapmali()
    {
        // Uygulama çöküp yeniden başladığında, DB'de Recording kalan satırlar
        // Interrupted'a çekilmeli.
        using var db = CreateDb();
        var svc = CreateService(db);
        var id = svc.InsertCapture(CaptureType.Video, "/storage/yarida_kaldi.mp4",
            DateTimeOffset.UtcNow.AddMinutes(-10), "BTN",
            status: CaptureStatus.Recording);

        // contentRootPath olarak temp klasörü veriyoruz — dosya orada yok,
        // ama ReconcileStaleRecordings bunu tolere eder (DateTimeOffset.UtcNow kullanır).
        var count = svc.ReconcileStaleRecordings(Path.GetTempPath());

        Assert.Equal(1, count);
        var entity = db.MediaCaptures.Find(id)!;
        Assert.Equal(CaptureStatus.Interrupted, entity.Status);
        Assert.NotNull(entity.EndedAt);
        Assert.True(entity.DurationMs >= 0);
    }

    [Fact]
    public void ReconcileStaleRecordings_HiçRecordingYoksa_SifirDonmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        svc.InsertCapture(CaptureType.Photo, "/s/foto.jpg",
            DateTimeOffset.UtcNow, "BTN", status: CaptureStatus.Completed);

        var count = svc.ReconcileStaleRecordings(Path.GetTempPath());

        Assert.Equal(0, count);
    }

    // -----------------------------------------------------------------------
    // UpdateFileMetadata
    // -----------------------------------------------------------------------

    [Fact]
    public void UpdateFileMetadata_MevcutId_AlanlarGuncellenmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);
        var id = svc.InsertCapture(CaptureType.Video, "/s/v.mp4",
            DateTimeOffset.UtcNow, "BTN");

        var result = svc.UpdateFileMetadata(id,
            width: 1280, height: 720, frameCount: 600, fileSizeBytes: 20_000_000);

        Assert.True(result);
        var entity = db.MediaCaptures.Find(id)!;
        Assert.Equal(1280, entity.Width);
        Assert.Equal(720, entity.Height);
        Assert.Equal(600, entity.FrameCount);
        Assert.Equal(20_000_000, entity.FileSizeBytes);
    }

    [Fact]
    public void UpdateFileMetadata_YanlisId_FalseDonmeli()
    {
        using var db = CreateDb();
        var svc = CreateService(db);

        var result = svc.UpdateFileMetadata(77777, 640, 480, 100, 1024);

        Assert.False(result);
    }

    [Fact]
    public void UpdateFileMetadata_NullGecilen_AlanlarDegismemeli()
    {
        // Null geçilen alanlar mevcut değeri ezmemeli.
        using var db = CreateDb();
        var svc = CreateService(db);
        var id = svc.InsertCapture(CaptureType.Video, "/s/v.mp4",
            DateTimeOffset.UtcNow, "BTN",
            width: 1920, height: 1080);

        svc.UpdateFileMetadata(id, width: null, height: null,
            frameCount: 999, fileSizeBytes: null);

        var entity = db.MediaCaptures.Find(id)!;
        // Width ve Height değişmemeli (null geçildi).
        Assert.Equal(1920, entity.Width);
        Assert.Equal(1080, entity.Height);
        // FrameCount güncellenmeli.
        Assert.Equal(999, entity.FrameCount);
    }
}
