using System.Text;
using Endoscopy.Models;
using Endoscopy.Services;
using Microsoft.AspNetCore.Mvc;

namespace Endoscopy.Controllers;

public record CaptureRequest(
    string? TriggerSource,
    string? PatientIdentifier = null,
    string? PatientName = null,
    string? DoctorName = null,
    string? ProcedureType = null,
    string? RoomName = null,
    string? CreatedByName = null)
{
    public CaptureContext ToContext() => new(PatientIdentifier, PatientName, DoctorName, ProcedureType, RoomName, CreatedByName);
}

[ApiController]
[Route("api")]
public class CapturesController : ControllerBase
{
    private readonly CameraService _camera;
    private readonly CaptureDbService _db;
    private readonly DeviceIdentityService _device;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CapturesController> _logger;

    public CapturesController(CameraService camera, CaptureDbService db, DeviceIdentityService device, IWebHostEnvironment env, ILogger<CapturesController> logger)
    {
        _camera = camera;
        _db = db;
        _device = device;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Canlı kamera akışı (MJPEG). &lt;img src="/api/video-feed"&gt; ile
    /// doğrudan tarayıcıda oynatılabilir; endoskopi kamerasının USB
    /// üzerinden canlı yayınına karşılık gelir.
    /// </summary>
    [HttpGet("video-feed")]
    public async Task VideoFeed()
    {
        var token = HttpContext.RequestAborted;
        Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

        try
        {
            while (!token.IsCancellationRequested)
            {
                var jpeg = _camera.GetLatestFrameJpeg();
                if (jpeg != null)
                {
                    var header = Encoding.ASCII.GetBytes(
                        $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");

                    await Response.Body.WriteAsync(header, token);
                    await Response.Body.WriteAsync(jpeg, token);
                    await Response.Body.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), token);
                    await Response.Body.FlushAsync(token);
                }

                await Task.Delay(33, token); // ~30 FPS
            }
        }
        catch (OperationCanceledException)
        {
            // Tarayıcı sekmesi kapandı / sayfa yenilendi -> normal davranış
        }
    }

    /// <summary>
    /// Anlık kareyi yakalar (Snapshot). Gerçek cihazda bu tetikleyici seri
    /// porttan ("PEDAL_CLICK") gelir; burada web arayüzündeki butondan gelir.
    /// Kare diske .jpg olarak yazılır ve SQLite'a satır olarak eklenir.
    /// </summary>
    [HttpPost("capture")]
    public IActionResult Capture([FromBody] CaptureRequest? request)
    {
        if (!_camera.IsCameraAvailable)
        {
            return StatusCode(500, new { message = "Kamera bulunamadı ya da başka bir uygulama tarafından kullanılıyor." });
        }

        using var frame = _camera.GetLatestFrameClone();
        if (frame == null || frame.Empty())
        {
            return StatusCode(500, new { message = "Kameradan henüz bir görüntü karesi alınamadı, birkaç saniye sonra tekrar deneyin." });
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var fileName = $"capture_{capturedAt.ToUnixTimeMilliseconds()}.jpg";

        var storageDir = Path.Combine(_env.ContentRootPath, "storage");
        Directory.CreateDirectory(storageDir);
        var absoluteFilePath = Path.Combine(storageDir, fileName);
        frame.SaveImage(absoluteFilePath);

        var relativePath = $"/storage/{fileName}";
        var fileSizeBytes = new FileInfo(absoluteFilePath).Length;
        var id = _db.InsertCapture(CaptureType.Photo, relativePath, capturedAt, request?.TriggerSource ?? "WEB_BUTTON", context: request?.ToContext(), fileSizeBytes: fileSizeBytes, width: frame.Width, height: frame.Height,
            machineName: _device.MachineName, localIpAddress: _device.LocalIpAddress, localMacAddress: _device.LocalMacAddress);

        // Dosya artık tam yazılmış durumda (fotoğrafta stream açık kalmıyor,
        // videodan farklı olarak) — bu yüzden metadata'yı hemen, aynı istekte
        // yazabiliyoruz. Başarısız olursa capture akışını bozmaz, sadece loglanır.
        FileIdentityTagger.TryWriteCaptureIdentity(absoluteFilePath, _device.MachineName, _device.LocalIpAddress, _device.LocalMacAddress, _logger);

        _logger.LogInformation("Yeni kare yakalandı: Id={Id}, Dosya={FileName}, {Width}x{Height}", id, fileName, frame.Width, frame.Height);

        return Ok(new { id, filePath = relativePath, capturedAt });
    }

    /// <summary>
    /// Video kaydını başlatır. Fotoğraftan farklı olarak tek istekte bitmez:
    /// bu, DB'de Status='recording' bir satır açar ve CameraService'e her kareyi
    /// tek bir dosyaya diske yazmasını söyler; asıl kayıt "stop" isteği gelene
    /// kadar arka planda, capture loop'un içinden, kare kare devam eder.
    /// </summary>
    [HttpPost("capture/video/start")]
    public IActionResult StartVideoCapture([FromBody] CaptureRequest? request)
    {
        if (!_camera.IsCameraAvailable)
        {
            return StatusCode(500, new { message = "Kamera bulunamadı ya da başka bir uygulama tarafından kullanılıyor." });
        }

        if (_camera.IsRecording)
        {
            return Conflict(new { message = "Zaten devam eden bir video kaydı var. Önce onu durdurun." });
        }

        var startedAt = DateTimeOffset.UtcNow;
        var baseFileName = $"video_{startedAt.ToUnixTimeMilliseconds()}";

        var storageDir = Path.Combine(_env.ContentRootPath, "storage");
        Directory.CreateDirectory(storageDir);

        // Önce kamerayı/codec'i başlatmayı deniyoruz; ancak başarılı olursa DB
        // satırını açıyoruz. Böylece kamera hatasında yarım kalan bir DB satırı
        // oluşmuyor (rollback'e gerek kalmıyor).
        var actualFilePath = _camera.StartRecording(storageDir, baseFileName);
        if (actualFilePath == null)
        {
            var reason = _camera.LastStartFailureReason ?? "Video kaydı başlatılamadı (codec ya da dosya hatası).";
            return StatusCode(500, new { message = reason });
        }

        var relativePath = $"/storage/{Path.GetFileName(actualFilePath)}";
        // Dosyanın kendi metadata'sına burada YAZMIYORUZ: VideoWriter dosyayı hâlâ
        // açık tutuyor, kayıt sürerken TagLibSharp ile açmaya çalışmak dosya
        // kilidiyle çakışır. Kimlik bilgisi DB'ye şimdi yazılıyor, dosyaya ise
        // kayıt kapandığında (StopVideoCapture / otomatik durma) yazılacak.
        var id = _db.InsertCapture(CaptureType.Video, relativePath, startedAt, request?.TriggerSource ?? "WEB_BUTTON", status: CaptureStatus.Recording, context: request?.ToContext(),
            machineName: _device.MachineName, localIpAddress: _device.LocalIpAddress, localMacAddress: _device.LocalMacAddress);

        _logger.LogInformation("Video kaydı başladı: Id={Id}, Dosya={FileName}", id, relativePath);

        return Ok(new { id, filePath = relativePath, startedAt });
    }

    /// <summary>
    /// Devam eden video kaydını durdurur, dosyayı finalize eder, dosyanın gerçekten
    /// oynatılabilir olduğunu doğrular ve DB satırını buna göre günceller:
    /// doğrulama başarılıysa Status='completed', başarısızsa Status='corrupted'.
    /// </summary>
    [HttpPost("capture/video/stop")]
    public IActionResult StopVideoCapture()
    {
        if (!_camera.IsRecording)
        {
            return BadRequest(new { message = "Devam eden bir video kaydı yok." });
        }

        var recording = _db.GetActiveRecording();
        var stopResult = _camera.StopRecording();

        if (stopResult == null)
        {
            // Bu istek işlenirken kayıt başka bir sebeple (kamera koptu, disk doldu)
            // zaten otomatik durmuş olabilir. DB'yi burada tekrar güncellemiyoruz ki
            // oradaki (muhtemelen 'interrupted') durumu ezmeyelim.
            return Ok(new { message = "Kayıt zaten durmuştu (muhtemelen otomatik olarak durduruldu)." });
        }

        if (recording == null)
        {
            return Ok(new { message = "Kayıt durduruldu (eşleşen DB satırı bulunamadı)." });
        }

        var endedAt = DateTimeOffset.UtcNow;
        var durationMs = (long)(endedAt - recording.CapturedAt).TotalMilliseconds;
        var finalStatus = stopResult.IsPlaybackVerified ? CaptureStatus.Completed : CaptureStatus.Corrupted;

        _db.CompleteVideoCapture(recording.Id, endedAt, durationMs, finalStatus, stopResult.Width, stopResult.Height, stopResult.FrameCount, stopResult.FileSizeBytes);

        // Dosya artık finalize edildi (VideoWriter kapandı) — kimlik bilgisini
        // burada, kayıt anında InsertCapture ile DB'ye yazılmış olan aynı
        // değerlerden (recording entity üzerinden) dosyanın içine de yazıyoruz.
        // Doğrulama başarısız olsa (Corrupted) bile deniyoruz — dosya bozuksa
        // zaten TagLibSharp da açamayıp sessizce başarısız olacaktır.
        FileIdentityTagger.TryWriteCaptureIdentity(stopResult.FilePath, recording.MachineName ?? _device.MachineName, recording.LocalIpAddress, recording.LocalMacAddress, _logger);

        _logger.LogInformation(
            "Video kaydı durdu: Id={Id}, Süre={DurationMs}ms, {Width}x{Height}, {FrameCount} kare, {FileSizeBytes} byte, Doğrulandı={Verified}",
            recording.Id, durationMs, stopResult.Width, stopResult.Height, stopResult.FrameCount, stopResult.FileSizeBytes, stopResult.IsPlaybackVerified);

        return Ok(new
        {
            id = recording.Id,
            endedAt,
            durationMs,
            verified = stopResult.IsPlaybackVerified,
            width = stopResult.Width,
            height = stopResult.Height,
            frameCount = stopResult.FrameCount,
            fileSizeBytes = stopResult.FileSizeBytes
        });
    }

    /// <summary>
    /// Sayfa yenilendiğinde ya da tarayıcı yeniden açıldığında "hâlâ devam eden
    /// bir kayıt var mı?" diye sormak için. Kayıt server-side olduğu için
    /// tarayıcı kapansa bile kayıt durmaz; bu endpoint UI'ın durumu senkronlaması içindir.
    /// </summary>
    [HttpGet("capture/video/status")]
    public IActionResult GetVideoStatus()
    {
        return Ok(new { isRecording = _camera.IsRecording, capture = _db.GetActiveRecording() });
    }

    /// <summary>Aktif (silinmemiş) tüm kayıtları (fotoğraf + video) en yeniden eskiye listeler.</summary>
    [HttpGet("captures")]
    public IActionResult GetCaptures()
    {
        return Ok(_db.GetCaptures());
    }

    /// <summary>
    /// Bir kaydı siler — ama gerçekten değil: soft-delete (IsActive=false).
    /// Dosya diskten kaldırılmaz, DB satırı da kalıcı silinmez, sadece normal
    /// listelemeden gizlenir. Tıbbi kayıt olduğu için kalıcı/geri dönüşsüz
    /// silme bilerek desteklenmiyor.
    /// </summary>
    [HttpDelete("captures/{id}")]
    public IActionResult DeleteCapture(long id)
    {
        var deleted = _db.DeactivateCapture(id);
        if (!deleted)
        {
            return NotFound(new { message = $"Id={id} ile eşleşen kayıt bulunamadı." });
        }

        _logger.LogInformation("Capture silindi (soft-delete): Id={Id}", id);
        return Ok(new { id, message = "Kayıt listeden kaldırıldı (dosya ve DB satırı hâlâ duruyor)." });
    }

    /// <summary>
    /// Bir kaydın Width/Height/FrameCount/FileSizeBytes bilgisini, dosyanın
    /// kendisini YENİDEN OKUYARAK tazeler. "Bu alanlar DB'de eklenmeden önce
    /// kaydedilmiş eski dosyaları doldur" (backfill) ya da "bir şeyler
    /// yanlış görünüyor, dosyadan tekrar ölç" senaryoları için — Windows'un
    /// Özellikler penceresiyle bir ilgisi yok, doğrudan dosyayı OpenCV ile açıp
    /// ölçüyoruz (bkz. CameraService.ReadVideoFileMetadata).
    /// </summary>
    [HttpPost("captures/{id}/refresh-metadata")]
    public IActionResult RefreshMetadata(long id)
    {
        var capture = _db.GetById(id);
        if (capture == null)
        {
            return NotFound(new { message = $"Id={id} ile eşleşen kayıt bulunamadı." });
        }

        var physicalPath = Path.Combine(_env.ContentRootPath, capture.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(physicalPath))
        {
            return NotFound(new { message = "Dosya diskte bulunamadı, metadata yenilenemedi (silinmiş/taşınmış olabilir)." });
        }

        var fileSizeBytes = new FileInfo(physicalPath).Length;
        int? width = null, height = null;
        long? frameCount = null;

        if (capture.CaptureType == CaptureType.Video)
        {
            var meta = _camera.ReadVideoFileMetadata(physicalPath);
            if (meta != null)
            {
                (width, height, frameCount) = meta.Value;
            }
        }

        _db.UpdateFileMetadata(id, width, height, frameCount, fileSizeBytes);

        _logger.LogInformation(
            "Metadata yenilendi: Id={Id}, {Width}x{Height}, {FrameCount} kare, {FileSizeBytes} byte",
            id, width, height, frameCount, fileSizeBytes);

        return Ok(new { id, width, height, frameCount, fileSizeBytes });
    }
}
