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

public record AnnotateRequest(string ImageBase64, string? Label = null);

public record MarkerRequest(string? Label = null, long? TimestampMs = null);

[ApiController]
[Route("api")]
public class CapturesController : ControllerBase
{
    // Tarayıcı tarafında (index.html / camera-ws-test.html) kare gönderme
    // aralığı setInterval(..., 200) yani 200ms = saniyede 5 kare. VideoWriter'a
    // GERÇEKTE üretilen bu hızı bildirmemiz lazım — aksi halde (örn. varsayılan
    // 20fps ile yazarsak) dosyada "20fps'lik" diye damgalanan ama aslında 5fps
    // hızında üretilmiş kareler olur, oynatıcı bunu 4 kat hızlandırılmış oynatır.
    // Tarayıcıdaki gönderim hızı değişirse BU DEĞER DE değişmeli.
    private const int WebSocketCameraFps = 5;

    private readonly CameraSessionManager _sessionManager;
    private readonly CodecDetector _codecDetector;
    private readonly CaptureDbService _db;
    private readonly DeviceIdentityService _device;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CapturesController> _logger;

    public CapturesController(CameraSessionManager sessionManager, CodecDetector codecDetector, CaptureDbService db, DeviceIdentityService device, IWebHostEnvironment env, ILogger<CapturesController> logger)
    {
        _sessionManager = sessionManager;
        _codecDetector = codecDetector;
        _db = db;
        _device = device;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Bir odanın canlı kamera akışı (MJPEG). &lt;img src="/api/rooms/oda1/video-feed"&gt;
    /// ile doğrudan tarayıcıda oynatılabilir. {roomId}, o odanın tarayıcısının
    /// /ws-camera/{roomId} üzerinden gönderdiği en son kareyi gösterir.
    /// </summary>
    [HttpGet("rooms/{roomId}/video-feed")]
    public async Task VideoFeed(string roomId)
    {
        var token = HttpContext.RequestAborted;
        Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Her turda tekrar bakıyoruz (bir kere değil): oda henüz hiç
                // bağlanmamışsa session null olabilir, sonradan bağlanınca
                // akış otomatik başlasın diye.
                var session = _sessionManager.TryGet(roomId);
                var jpeg = session?.GetLatestFrameJpeg();
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
    /// Bir odanın anlık karesini yakalar (Snapshot). Kare diske .jpg olarak
    /// yazılır ve DB'ye RoomName=roomId ile eklenir.
    /// </summary>
    [HttpPost("rooms/{roomId}/capture")]
    public IActionResult Capture(string roomId, [FromBody] CaptureRequest? request)
    {
        var session = _sessionManager.TryGet(roomId);
        if (session == null || !session.IsLive)
        {
            return StatusCode(500, new { message = $"'{roomId}' odasından şu an görüntü alınamıyor (tarayıcı bağlı değil ya da henüz kare göndermedi)." });
        }

        using var frame = session.GetLatestFrameClone();
        if (frame == null || frame.Empty())
        {
            return StatusCode(500, new { message = "Kameradan henüz bir görüntü karesi alınamadı, birkaç saniye sonra tekrar deneyin." });
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var fileName = $"capture_{roomId}_{capturedAt.ToUnixTimeMilliseconds()}.jpg";

        var storageDir = Path.Combine(_env.ContentRootPath, "storage");
        Directory.CreateDirectory(storageDir);
        var absoluteFilePath = Path.Combine(storageDir, fileName);
        frame.SaveImage(absoluteFilePath);

        var relativePath = $"/storage/{fileName}";
        var fileSizeBytes = new FileInfo(absoluteFilePath).Length;
        // RoomName'i route'tan (roomId) alıyoruz, istemcinin gönderdiği (varsa)
        // CaptureRequest.RoomName'i EZİYORUZ — hangi odadan geldiği, tarayıcının
        // hangi WebSocket'e bağlandığından (yani route'tan) bellidir, forma
        // güvenmiyoruz (tıpkı MachineName/IP/MAC'in de istemciden değil
        // sunucudan gelmesi gibi).
        var context = (request?.ToContext() ?? new CaptureContext()) with { RoomName = roomId };
        var id = _db.InsertCapture(CaptureType.Photo, relativePath, capturedAt, request?.TriggerSource ?? "WEB_BUTTON", context: context, fileSizeBytes: fileSizeBytes, width: frame.Width, height: frame.Height,
            machineName: _device.MachineName, localIpAddress: _device.LocalIpAddress, localMacAddress: _device.LocalMacAddress);

        FileIdentityTagger.TryWriteCaptureIdentity(absoluteFilePath, _device.MachineName, _device.LocalIpAddress, _device.LocalMacAddress, _logger);

        _logger.LogInformation("Yeni kare yakalandı: Id={Id}, Oda={RoomId}, Dosya={FileName}, {Width}x{Height}", id, roomId, fileName, frame.Width, frame.Height);

        return Ok(new { id, filePath = relativePath, capturedAt });
    }

    /// <summary>
    /// Bir odanın video kaydını başlatır. DB'de Status='recording' bir satır
    /// açar; asıl kayıt "stop" isteği gelene (ya da bağlantı kopana) kadar
    /// arka planda, o odanın WebSocket bağlantısından gelen her karede devam eder.
    /// </summary>
    [HttpPost("rooms/{roomId}/capture/video/start")]
    public IActionResult StartVideoCapture(string roomId, [FromBody] CaptureRequest? request)
    {
        var session = _sessionManager.GetOrCreate(roomId);
        if (!session.IsLive)
        {
            return StatusCode(500, new { message = $"'{roomId}' odasından şu an görüntü alınamıyor (tarayıcı bağlı değil ya da henüz kare göndermedi)." });
        }

        if (session.IsRecording)
        {
            return Conflict(new { message = "Bu odada zaten devam eden bir video kaydı var. Önce onu durdurun." });
        }

        var startedAt = DateTimeOffset.UtcNow;
        var baseFileName = $"video_{roomId}_{startedAt.ToUnixTimeMilliseconds()}";

        var storageDir = Path.Combine(_env.ContentRootPath, "storage");
        Directory.CreateDirectory(storageDir);

        // Önce kamerayı/codec'i başlatmayı deniyoruz; ancak başarılı olursa DB
        // satırını açıyoruz. Böylece hatada yarım kalan bir DB satırı oluşmuyor.
        var codec = _codecDetector.GetCodec();
        var actualFilePath = session.StartRecording(storageDir, baseFileName, codec, fps: WebSocketCameraFps);
        if (actualFilePath == null)
        {
            var reason = session.LastStartFailureReason ?? "Video kaydı başlatılamadı (codec ya da dosya hatası).";
            return StatusCode(500, new { message = reason });
        }

        var relativePath = $"/storage/{Path.GetFileName(actualFilePath)}";
        var context = (request?.ToContext() ?? new CaptureContext()) with { RoomName = roomId };
        var id = _db.InsertCapture(CaptureType.Video, relativePath, startedAt, request?.TriggerSource ?? "WEB_BUTTON", status: CaptureStatus.Recording, context: context,
            machineName: _device.MachineName, localIpAddress: _device.LocalIpAddress, localMacAddress: _device.LocalMacAddress);

        _logger.LogInformation("Video kaydı başladı: Id={Id}, Oda={RoomId}, Dosya={FileName}", id, roomId, relativePath);

        return Ok(new { id, filePath = relativePath, startedAt });
    }

    /// <summary>
    /// Bir odanın devam eden video kaydını durdurur, dosyayı finalize eder,
    /// gerçekten oynatılabilir olup olmadığını doğrular ve DB satırını buna göre
    /// günceller: doğrulama başarılıysa Status='completed', başarısızsa Status='corrupted'.
    /// </summary>
    [HttpPost("rooms/{roomId}/capture/video/stop")]
    public IActionResult StopVideoCapture(string roomId)
    {
        var session = _sessionManager.TryGet(roomId);
        if (session == null || !session.IsRecording)
        {
            return BadRequest(new { message = "Devam eden bir video kaydı yok." });
        }

        var recording = _db.GetActiveRecording(roomId);
        var stopResult = session.StopRecording();

        if (stopResult == null)
        {
            // Bu istek işlenirken kayıt başka bir sebeple (bağlantı koptu, disk
            // doldu) zaten otomatik durmuş olabilir. DB'yi burada tekrar
            // güncellemiyoruz ki oradaki (muhtemelen 'interrupted') durumu ezmeyelim.
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

        FileIdentityTagger.TryWriteCaptureIdentity(stopResult.FilePath, recording.MachineName ?? _device.MachineName, recording.LocalIpAddress, recording.LocalMacAddress, _logger);

        _logger.LogInformation(
            "Video kaydı durdu: Id={Id}, Oda={RoomId}, Süre={DurationMs}ms, {Width}x{Height}, {FrameCount} kare, {FileSizeBytes} byte, Doğrulandı={Verified}",
            recording.Id, roomId, durationMs, stopResult.Width, stopResult.Height, stopResult.FrameCount, stopResult.FileSizeBytes, stopResult.IsPlaybackVerified);

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
    /// Bir odanın "hâlâ kayıt devam ediyor mu, kamera canlı mı" durumunu döner.
    /// Sayfa yenilendiğinde UI'ın durumu senkronlaması içindir.
    /// </summary>
    [HttpGet("rooms/{roomId}/capture/video/status")]
    public IActionResult GetVideoStatus(string roomId)
    {
        var session = _sessionManager.TryGet(roomId);
        return Ok(new
        {
            isRecording = session?.IsRecording ?? false,
            isLive = session?.IsLive ?? false,
            capture = _db.GetActiveRecording(roomId)
        });
    }

    /// <summary>
    /// Kayıtları en yeniden eskiye listeler. "roomId" verilmezse TÜM odaların
    /// kayıtları birlikte döner (admin sayfası bunu kullanır); "roomId" verilirse
    /// sadece o odanın kayıtları döner (oda ekranı, index.html, kendi odasıyla
    /// filtreleyerek çağırır — başka odanın/hastanın kaydını görmesin diye).
    /// </summary>
    [HttpGet("captures")]
    public IActionResult GetCaptures([FromQuery] string? roomId = null)
    {
        return Ok(_db.GetCaptures(roomId));
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
    /// kendisini YENİDEN OKUYARAK tazeler (backfill / manuel düzeltme).
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
            var meta = VideoFileMetadataReader.Read(physicalPath);
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

    /// <summary>
    /// Bir fotoğraf kaydının üzerinde yapılan çizim ve ölçümleri yeni bir
    /// işaretli fotoğraf (TriggerSource=ANNOTATION) olarak kaydeder. Orijinal
    /// fotoğraf korunur.
    /// </summary>
    [HttpPost("captures/{id}/annotate")]
    public async Task<IActionResult> SaveAnnotatedCapture(long id, [FromBody] AnnotateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            return BadRequest(new { message = "Görüntü verisi (ImageBase64) boş olamaz." });
        }

        var original = _db.GetById(id);
        if (original == null)
        {
            return NotFound(new { message = $"Id={id} ile eşleşen kayıt bulunamadı." });
        }

        try
        {
            var base64Data = request.ImageBase64;
            var commaIdx = base64Data.IndexOf(',');
            if (commaIdx >= 0)
            {
                base64Data = base64Data.Substring(commaIdx + 1);
            }

            var imageBytes = Convert.FromBase64String(base64Data);

            var capturedAt = DateTimeOffset.UtcNow;
            var roomId = original.RoomName ?? "oda1";
            var fileName = $"annotated_{roomId}_{capturedAt.ToUnixTimeMilliseconds()}.jpg";

            var storageDir = Path.Combine(_env.ContentRootPath, "storage");
            Directory.CreateDirectory(storageDir);
            var absoluteFilePath = Path.Combine(storageDir, fileName);

            await System.IO.File.WriteAllBytesAsync(absoluteFilePath, imageBytes);

            var relativePath = $"/storage/{fileName}";
            var fileSizeBytes = imageBytes.Length;

            int? width = original.Width;
            int? height = original.Height;
            try
            {
                using var mat = OpenCvSharp.Cv2.ImDecode(imageBytes, OpenCvSharp.ImreadModes.Color);
                if (mat != null && !mat.Empty())
                {
                    width = mat.Width;
                    height = mat.Height;
                }
            }
            catch { /* fallback to original dimensions */ }

            var context = new CaptureContext(
                original.PatientIdentifier,
                original.PatientName,
                original.DoctorName,
                original.ProcedureType,
                original.RoomName,
                original.CreatedByName);

            var newId = _db.InsertCapture(
                CaptureType.Photo,
                relativePath,
                capturedAt,
                triggerSource: "ANNOTATION",
                context: context,
                fileSizeBytes: fileSizeBytes,
                width: width,
                height: height,
                machineName: _device.MachineName,
                localIpAddress: _device.LocalIpAddress,
                localMacAddress: _device.LocalMacAddress);

            FileIdentityTagger.TryWriteCaptureIdentity(absoluteFilePath, _device.MachineName, _device.LocalIpAddress, _device.LocalMacAddress, _logger);

            _logger.LogInformation("İşaretli fotoğraf kaydedildi: Id={NewId}, OrijinalId={OrigId}, Dosya={FileName}", newId, id, fileName);

            return Ok(new
            {
                id = newId,
                originalId = id,
                filePath = relativePath,
                capturedAt,
                width,
                height,
                fileSizeBytes,
                triggerSource = "ANNOTATION"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "İşaretli fotoğraf kaydedilemedi: Id={Id}", id);
            return StatusCode(500, new { message = "Fotoğraf kaydedilirken sunucu hatası oluştu: " + ex.Message });
        }
    }

    /// <summary>
    /// Devam eden video kaydına anlık zaman damgalı marker/işaret ekler.
    /// Kayıt sürerken pedal, klavye kısayolu (M) veya arayüz butonuyla çağrılır.
    /// </summary>
    [HttpPost("rooms/{roomId}/capture/video/marker")]
    public IActionResult AddLiveMarker(string roomId, [FromBody] MarkerRequest? request)
    {
        var recording = _db.GetActiveRecording(roomId);
        if (recording == null)
        {
            return BadRequest(new { message = $"'{roomId}' odasında devam eden aktif bir video kaydı bulunamadı." });
        }

        var now = DateTimeOffset.UtcNow;
        var elapsedMs = Math.Max(0, (long)(now - recording.CapturedAt).TotalMilliseconds);
        var label = string.IsNullOrWhiteSpace(request?.Label) ? "Önemli Bulgu" : request.Label.Trim();

        var marker = _db.AddMarker(recording.Id, elapsedMs, label);
        if (marker == null)
        {
            return StatusCode(500, new { message = "Marker kaydedilemedi." });
        }

        return Ok(new
        {
            id = marker.Id,
            mediaCaptureId = marker.MediaCaptureId,
            timestampMs = marker.TimestampMs,
            label = marker.Label,
            createdAt = marker.CreatedAt
        });
    }

    /// <summary>
    /// Tamamlanmış veya izlenmekte olan bir video kaydına belirli bir zaman damgasıyla marker ekler.
    /// </summary>
    [HttpPost("captures/{id}/markers")]
    public IActionResult AddCaptureMarker(long id, [FromBody] MarkerRequest request)
    {
        var capture = _db.GetById(id);
        if (capture == null)
        {
            return NotFound(new { message = $"Id={id} olan video kaydı bulunamadı." });
        }

        if (capture.CaptureType != CaptureType.Video)
        {
            return BadRequest(new { message = "Sadece video kayıtlarına marker eklenebilir." });
        }

        var timestampMs = request.TimestampMs ?? 0;
        var label = string.IsNullOrWhiteSpace(request.Label) ? "İşaret" : request.Label.Trim();

        var marker = _db.AddMarker(id, timestampMs, label);
        if (marker == null)
        {
            return StatusCode(500, new { message = "Marker eklenemedi." });
        }

        return Ok(new
        {
            id = marker.Id,
            mediaCaptureId = marker.MediaCaptureId,
            timestampMs = marker.TimestampMs,
            label = marker.Label,
            createdAt = marker.CreatedAt
        });
    }

    /// <summary>
    /// Bir video kaydına ait tüm zaman damgalı marker'ları listeler.
    /// </summary>
    [HttpGet("captures/{id}/markers")]
    public IActionResult GetCaptureMarkers(long id)
    {
        var markers = _db.GetMarkers(id);
        return Ok(markers);
    }

    /// <summary>
    /// Bir marker'ı kimliğine göre siler.
    /// </summary>
    [HttpDelete("markers/{markerId}")]
    public IActionResult DeleteMarker(long markerId)
    {
        var deleted = _db.DeleteMarker(markerId);
        if (!deleted)
        {
            return NotFound(new { message = $"Marker Id={markerId} bulunamadı." });
        }
        return Ok(new { markerId, message = "Marker silindi." });
    }
}
