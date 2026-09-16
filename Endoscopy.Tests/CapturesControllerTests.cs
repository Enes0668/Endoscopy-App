using Endoscopy.Controllers;
using Endoscopy.Data;
using Endoscopy.Models;
using Endoscopy.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Endoscopy.Tests;

/// <summary>
/// CapturesController'ın HTTP endpoint mantığını test eder.
///
/// Controller somut bağımlılıklar (CameraSessionManager, CaptureDbService vb.)
/// aldığından bunları arayüz arkasına almak için refactor gerekirdi. Biz yerine
/// gerçek (ama izole) örnekler kullanıyoruz:
///   - CameraSessionManager → gerçek, mock ILoggerFactory ile
///   - CaptureDbService     → gerçek, InMemory DB ile
///   - CodecDetector        → gerçek, mock logger ile
///   - DeviceIdentityService → gerçek, mock logger ile
///   - IWebHostEnvironment  → mock (arayüz olduğu için Moq ile)
///   - ILogger              → mock
/// Bu yaklaşım "unit" ile "entegrasyon" testi arasında bir yerde durur —
/// dış sistem (PostgreSQL, gerçek kamera) yok ama servisler gerçek çalışıyor.
/// </summary>
public class CapturesControllerTests
{
    // -----------------------------------------------------------------------
    // Yardımcı fabrika
    // -----------------------------------------------------------------------

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private record ControllerBundle(
        CapturesController Controller,
        CameraSessionManager SessionManager,
        CaptureDbService DbService,
        AppDbContext Db,
        Mock<IWebHostEnvironment> EnvMock);

    private static ControllerBundle CreateController(string contentRootPath = "")
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);

        var sessionManager = new CameraSessionManager(loggerFactory.Object);
        var codecDetector  = new CodecDetector(new Mock<ILogger<CodecDetector>>().Object);
        var db             = CreateDb();
        var dbService      = new CaptureDbService(db, new Mock<ILogger<CaptureDbService>>().Object);
        var device         = new DeviceIdentityService(new Mock<ILogger<DeviceIdentityService>>().Object);
        var controllerLogger = new Mock<ILogger<CapturesController>>().Object;

        var envMock = new Mock<IWebHostEnvironment>();
        envMock.Setup(e => e.ContentRootPath).Returns(
            string.IsNullOrEmpty(contentRootPath) ? Path.GetTempPath() : contentRootPath);

        var controller = new CapturesController(
            sessionManager, codecDetector, dbService, device, envMock.Object, controllerLogger);

        return new ControllerBundle(controller, sessionManager, dbService, db, envMock);
    }

    // -----------------------------------------------------------------------
    // GET /api/captures
    // -----------------------------------------------------------------------

    [Fact]
    public void GetCaptures_HicKayitYok_BosListeDoner()
    {
        var b = CreateController();

        var result = b.Controller.GetCaptures() as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal(200, result!.StatusCode);
    }

    [Fact]
    public void GetCaptures_RoomIdNull_TumOdalariDoner()
    {
        var b = CreateController();
        b.DbService.InsertCapture(CaptureType.Photo, "/s/a.jpg",
            DateTimeOffset.UtcNow, "BTN", context: new CaptureContext(RoomName: "oda1"));
        b.DbService.InsertCapture(CaptureType.Photo, "/s/b.jpg",
            DateTimeOffset.UtcNow, "BTN", context: new CaptureContext(RoomName: "oda2"));

        var result = b.Controller.GetCaptures(roomId: null) as OkObjectResult;
        var list   = result!.Value as List<CaptureListItem>;

        Assert.Equal(2, list!.Count);
    }

    [Fact]
    public void GetCaptures_RoomIdVerilince_SadeceOOdayiDoner()
    {
        var b = CreateController();
        b.DbService.InsertCapture(CaptureType.Photo, "/s/a.jpg",
            DateTimeOffset.UtcNow, "BTN", context: new CaptureContext(RoomName: "oda1"));
        b.DbService.InsertCapture(CaptureType.Photo, "/s/b.jpg",
            DateTimeOffset.UtcNow, "BTN", context: new CaptureContext(RoomName: "oda2"));

        var result = b.Controller.GetCaptures(roomId: "oda1") as OkObjectResult;
        var list   = result!.Value as List<CaptureListItem>;

        Assert.Single(list!);
        Assert.Equal("oda1", list![0].RoomName);
    }

    // -----------------------------------------------------------------------
    // DELETE /api/captures/{id}
    // -----------------------------------------------------------------------

    [Fact]
    public void DeleteCapture_MevcutId_200Doner()
    {
        var b  = CreateController();
        var id = b.DbService.InsertCapture(CaptureType.Photo, "/s/foto.jpg",
            DateTimeOffset.UtcNow, "BTN");

        var result = b.Controller.DeleteCapture(id) as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal(200, result!.StatusCode);
    }

    [Fact]
    public void DeleteCapture_YanlisId_404Doner()
    {
        var b = CreateController();

        var result = b.Controller.DeleteCapture(99999) as NotFoundObjectResult;

        Assert.NotNull(result);
        Assert.Equal(404, result!.StatusCode);
    }

    [Fact]
    public void DeleteCapture_SonrasindaKayitGizlenir()
    {
        var b  = CreateController();
        var id = b.DbService.InsertCapture(CaptureType.Photo, "/s/foto.jpg",
            DateTimeOffset.UtcNow, "BTN");

        b.Controller.DeleteCapture(id);

        // Soft-delete sonrasında liste boş gelmeli.
        var result = b.Controller.GetCaptures() as OkObjectResult;
        var list   = result!.Value as List<CaptureListItem>;
        Assert.Empty(list!);
    }

    // -----------------------------------------------------------------------
    // GET /api/rooms/{roomId}/capture/video/status
    // -----------------------------------------------------------------------

    [Fact]
    public void GetVideoStatus_OdaHicBaslanmadiysa_RecordingVeLiveFalse()
    {
        var b = CreateController();

        var result = b.Controller.GetVideoStatus("olmayan-oda") as OkObjectResult;

        Assert.NotNull(result);
        // Anonim nesne cross-assembly'de dynamic ile okunamaz; JSON'a çevirip okuyoruz.
        var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("isRecording").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("isLive").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("capture").ValueKind);
    }

    [Fact]
    public void GetVideoStatus_OdaVarAmaKaydYok_RecordingFalse()
    {
        var b = CreateController();
        b.SessionManager.GetOrCreate("oda1");

        var result = b.Controller.GetVideoStatus("oda1") as OkObjectResult;

        var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("isRecording").GetBoolean());
    }

    // -----------------------------------------------------------------------
    // POST /api/rooms/{roomId}/capture/video/start
    // -----------------------------------------------------------------------

    [Fact]
    public void StartVideoCapture_OdaCanliDegil_500Doner()
    {
        // Kamera bağlı değil (hiç frame gelmedi) → 500 bekliyoruz.
        var b = CreateController();

        var result = b.Controller.StartVideoCapture("oda1", null) as ObjectResult;

        Assert.NotNull(result);
        Assert.Equal(500, result!.StatusCode);
    }

    [Fact]
    public void StartVideoCapture_OdaCanliAmaZatenKaydYapıyor_409Doner()
    {
        // Session "canlı" ve "kayıt var" modunda ayarlıyoruz (reflection).
        var b       = CreateController();
        var session = b.SessionManager.GetOrCreate("oda1");

        SetSessionLive(session);
        SetSessionRecording(session, true);

        var result = b.Controller.StartVideoCapture("oda1", null) as ObjectResult;

        Assert.NotNull(result);
        Assert.Equal(409, result!.StatusCode); // Conflict
    }

    // -----------------------------------------------------------------------
    // POST /api/rooms/{roomId}/capture/video/stop
    // -----------------------------------------------------------------------

    [Fact]
    public void StopVideoCapture_OdaYok_400Doner()
    {
        // Hiç oturum oluşturulmamış → stop isteği BadRequest olmalı.
        var b = CreateController();

        var result = b.Controller.StopVideoCapture("olmayan-oda") as ObjectResult;

        Assert.NotNull(result);
        Assert.Equal(400, result!.StatusCode);
    }

    [Fact]
    public void StopVideoCapture_OdaVarAmaKaydYok_400Doner()
    {
        var b = CreateController();
        b.SessionManager.GetOrCreate("oda1"); // session var, ama kayıt yok

        var result = b.Controller.StopVideoCapture("oda1") as ObjectResult;

        Assert.NotNull(result);
        Assert.Equal(400, result!.StatusCode);
    }

    // -----------------------------------------------------------------------
    // POST /api/rooms/{roomId}/capture  (fotoğraf)
    // -----------------------------------------------------------------------

    [Fact]
    public void Capture_OdaYok_500Doner()
    {
        // Hiç oturum yok → 500 bekliyoruz.
        var b = CreateController();

        var result = b.Controller.Capture("olmayan-oda", null) as ObjectResult;

        Assert.NotNull(result);
        Assert.Equal(500, result!.StatusCode);
    }

    [Fact]
    public void Capture_OdaVarAmaCanliDegil_500Doner()
    {
        // Session var ama hiç frame gelmedi → 500.
        var b = CreateController();
        b.SessionManager.GetOrCreate("oda1");

        var result = b.Controller.Capture("oda1", null) as ObjectResult;

        Assert.NotNull(result);
        Assert.Equal(500, result!.StatusCode);
    }

    // -----------------------------------------------------------------------
    // POST /api/captures/{id}/refresh-metadata
    // -----------------------------------------------------------------------

    [Fact]
    public void RefreshMetadata_YanlisId_404Doner()
    {
        var b = CreateController();

        var result = b.Controller.RefreshMetadata(99999) as NotFoundObjectResult;

        Assert.NotNull(result);
        Assert.Equal(404, result!.StatusCode);
    }

    [Fact]
    public void RefreshMetadata_DosyaDiskteYok_404Doner()
    {
        // DB kaydı var ama dosya diskte yok → 404.
        var b  = CreateController();
        var id = b.DbService.InsertCapture(CaptureType.Video,
            "/storage/olmayan_video.mp4", DateTimeOffset.UtcNow, "BTN");

        var result = b.Controller.RefreshMetadata(id) as NotFoundObjectResult;

        Assert.NotNull(result);
        Assert.Equal(404, result!.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Video Marker Controller Tests
    // -----------------------------------------------------------------------

    [Fact]
    public void AddLiveMarker_KayitYokken_400Doner()
    {
        var b = CreateController();
        var result = b.Controller.AddLiveMarker("oda1", new MarkerRequest("Polip")) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal(400, result!.StatusCode);
    }

    [Fact]
    public void AddLiveMarker_KayitVarken_200Doner()
    {
        var b = CreateController();
        b.DbService.InsertCapture(CaptureType.Video, "/s/v.mp4", DateTimeOffset.UtcNow, "BTN",
            status: CaptureStatus.Recording, context: new CaptureContext(RoomName: "oda1"));

        var result = b.Controller.AddLiveMarker("oda1", new MarkerRequest("Kanama")) as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal(200, result!.StatusCode);
    }

    [Fact]
    public void AddCaptureMarker_VideoOlmayanId_400Doner()
    {
        var b = CreateController();
        var id = b.DbService.InsertCapture(CaptureType.Photo, "/s/f.jpg", DateTimeOffset.UtcNow, "BTN");

        var result = b.Controller.AddCaptureMarker(id, new MarkerRequest("Polip", 5000)) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal(400, result!.StatusCode);
    }

    [Fact]
    public void AddCaptureMarker_GecerliVideoya_200Doner()
    {
        var b = CreateController();
        var id = b.DbService.InsertCapture(CaptureType.Video, "/s/v.mp4", DateTimeOffset.UtcNow, "BTN");

        var result = b.Controller.AddCaptureMarker(id, new MarkerRequest("Polip", 5000)) as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal(200, result!.StatusCode);
    }

    [Fact]
    public void GetCaptureMarkers_ListeyiDoner()
    {
        var b = CreateController();
        var id = b.DbService.InsertCapture(CaptureType.Video, "/s/v.mp4", DateTimeOffset.UtcNow, "BTN");
        b.DbService.AddMarker(id, 1000, "M1");
        b.DbService.AddMarker(id, 2000, "M2");

        var result = b.Controller.GetCaptureMarkers(id) as OkObjectResult;
        var list = result!.Value as List<VideoMarkerDto>;

        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
    }

    [Fact]
    public void DeleteMarker_MevcutId_200Doner()
    {
        var b = CreateController();
        var id = b.DbService.InsertCapture(CaptureType.Video, "/s/v.mp4", DateTimeOffset.UtcNow, "BTN");
        var m = b.DbService.AddMarker(id, 1000, "M1")!;

        var result = b.Controller.DeleteMarker(m.Id) as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal(200, result!.StatusCode);
    }

    // -----------------------------------------------------------------------
    // Yardımcı reflection metodları
    // -----------------------------------------------------------------------

    /// <summary>CameraSession'ı "son frame geldi" durumuna getirir (IsLive = true).</summary>
    private static void SetSessionLive(CameraSession session)
    {
        var prop = typeof(CameraSession).GetProperty(
            "LastFrameAt",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        prop.SetValue(session, DateTimeOffset.UtcNow);
    }

    /// <summary>CameraSession'ın _isRecording alanını doğrudan set eder.</summary>
    private static void SetSessionRecording(CameraSession session, bool value)
    {
        var field = typeof(CameraSession).GetField(
            "_isRecording",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(session, value);
    }
}
