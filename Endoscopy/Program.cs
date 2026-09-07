using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using Endoscopy.Data;
using Endoscopy.Models;
using Endoscopy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Enum'ları ("photo", "recording" vb.) JSON'da metin olarak taşı; camelCase
// politikası "Photo" -> "photo" üretiyor, yani API sözleşmesi (frontend'in
// beklediği string değerler) hiç değişmedi.
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// Çoklu oda/kamera desteği: eski CameraService (tek fiziksel kamera, sunucunun
// kendi VideoCapture(0)'ı) kaldırıldı — artık her odanın kamerası kendi
// tarayıcısından WebSocket ile buraya akıtılıyor (bkz. /ws-camera/{roomId}
// endpoint'i ve Services/CameraSession.cs).
// CameraSessionManager, her oda için ayrı bir CameraSession tutan singleton
// kayıt defteri. CodecDetector de tüm odaların PAYLAŞTIĞI, bu makine için bir
// kere hesaplanan codec tespitini tutan singleton (bkz. Services/CodecDetector.cs).
builder.Services.AddSingleton<CameraSessionManager>();
builder.Services.AddSingleton<CodecDetector>();

// Bu istasyonun (bilgisayarın) kimliğini (MachineName/IP/MAC) bir kere
// tespit edip önbelleğe alan servis — bkz. Services/DeviceIdentityService.cs.
builder.Services.AddSingleton<DeviceIdentityService>();

// PostgreSQL üzerinde EF Core (bkz. Data/AppDbContext.cs, Models/). DbContext
// scoped yaşam süresine sahip olmalı (thread-safe değil) — CaptureDbService de
// bu yüzden scoped; her HTTP isteği kendi DbContext örneğini alır.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<CaptureDbService>();

// Prototipte tarayıcıdan (farklı porttan da olsa) rahatça test edebilmek için CORS açık.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// --- Uygulama açılışında: bekleyen EF Core migration'larını uygula (tablo yoksa
// oluşturur, şema değiştiyse günceller) ve crash recovery'yi çalıştır. CaptureDbService
// artık scoped olduğu için burada app.Services (root provider) üzerinden değil,
// ayrı bir scope açıp ondan çözülüyor.
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();

    // Önceki çalıştırmadan kalma yarım kalmış ("Status=Recording") video
    // kayıtlarını 'Interrupted' olarak işaretle. Bellekteki gerçek kayıt durumu
    // her zaman sıfırdan başladığı için, DB'de hâlâ "Recording" görünen bir
    // satır varsa bu kesin olarak önceki çalıştırmanın düzgün kapanmadığı
    // (çökme, zorla sonlandırma) anlamına gelir.
    scope.ServiceProvider.GetRequiredService<CaptureDbService>().ReconcileStaleRecordings(builder.Environment.ContentRootPath);
}

// --- Kayıt, kullanıcı "stop" demeden kendi kendine durursa (bağlantı koptu,
// disk doldu — bkz. CameraSession.NotifyConnectionClosed / AutoStopRecording)
// bunu burada DB'ye yansıtıyoruz. Normal "stop" akışından (CapturesController)
// farklı olarak burada bir HTTP isteği yok, o yüzden DB güncellemesi doğrudan
// event handler'da yapılıyor. Event her tetiklendiğinde kendi (kısa ömürlü)
// scope'unu açıyor — CaptureDbService scoped olduğu için tek bir örneği uzun
// süre elde tutmak doğru olmaz.
app.Services.GetRequiredService<CameraSessionManager>().RecordingAutoStopped += info =>
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CaptureDbService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Artık aynı anda birden fazla oda kayıt yapabildiği için, hangi odanın
    // (info.RoomId) kaydı durduysa SADECE onu arıyoruz — filtresiz sorgu
    // yanlış odanın satırını güncelleyebilirdi.
    var active = db.GetActiveRecording(info.RoomId);
    if (active == null)
    {
        logger.LogWarning("RecordingAutoStopped tetiklendi (Oda={RoomId}) ama DB'de eşleşen 'Recording' satırı bulunamadı.", info.RoomId);
        return;
    }

    var endedAt = DateTimeOffset.UtcNow;
    var durationMs = (long)(endedAt - active.CapturedAt).TotalMilliseconds;

    // Otomatik durma her zaman Interrupted — kullanıcının normal "stop"
    // akışından ayırt edilsin diye.
    db.CompleteVideoCapture(active.Id, endedAt, durationMs, CaptureStatus.Interrupted, info.Width, info.Height, info.FrameCount, info.FileSizeBytes);

    // Normal "stop" akışındaki (CapturesController.StopVideoCapture) aynı mantık:
    // dosya artık finalize edildi (VideoWriter kapandı), kimlik bilgisini şimdi
    // dosyanın içine de yazabiliriz.
    var device = scope.ServiceProvider.GetRequiredService<DeviceIdentityService>();
    FileIdentityTagger.TryWriteCaptureIdentity(info.FilePath, active.MachineName ?? device.MachineName, active.LocalIpAddress, active.LocalMacAddress, logger);

    logger.LogWarning(
        "Video kaydı otomatik durduruldu ve DB'de 'Interrupted' işaretlendi: Id={Id}, Oda={RoomId}, Sebep={Reason}, Doğrulandı={Verified}",
        active.Id, info.RoomId, info.Reason, info.IsPlaybackVerified);
};

// Configure the HTTP request pipeline.

// wwwroot altındaki index.html ve statik dosyaları (JS/CSS) sun.
app.UseDefaultFiles();
app.UseStaticFiles();

// Yakalanan (capture) fotoğrafların diskteki klasörünü /storage yolu üzerinden sun.
var storageDir = Path.Combine(builder.Environment.ContentRootPath, "storage");
Directory.CreateDirectory(storageDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(storageDir),
    RequestPath = "/storage"
});

app.UseCors();

app.UseAuthorization();

app.MapControllers();

// --- Her odanın kendi kamerasını (tarayıcı üzerinden, getUserMedia ile) buraya
// WebSocket üzerinden akıttığı yer. {roomId}, hangi odadan geldiğini ayırt eder
// (bkz. CameraSessionManager, CameraSession, ve DB tarafında MediaCapture.RoomName).
// Her bağlantı kendi CameraSession'ına yazıyor — odalar birbirinden tamamen
// izole, thread-safety kuralları CameraService'teki ile aynı (bkz. CameraSession.cs).
app.UseWebSockets();

app.Map("/ws-camera/{roomId}", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var roomId = context.Request.RouteValues["roomId"]?.ToString();
    if (string.IsNullOrWhiteSpace(roomId))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var sessionManager = context.RequestServices.GetRequiredService<CameraSessionManager>();
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var session = sessionManager.GetOrCreate(roomId);

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    logger.LogInformation("WebSocket kamera istemcisi bağlandı: Oda={RoomId}, IP={RemoteIp}", roomId, context.Connection.RemoteIpAddress);

    var buffer = new byte[64 * 1024];
    var frameCount = 0;

    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "İstemci tarafından kapatıldı.", CancellationToken.None);
                    logger.LogInformation("WebSocket kamera istemcisi bağlantıyı kapattı: Oda={RoomId}, Toplam {FrameCount} kare.", roomId, frameCount);
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            frameCount++;

            // Tarayıcıdan gelen JPEG byte'larını gerçek bir kareye (Mat) çözüyoruz —
            // böylece kayıt/foto akışı, eski CameraService'in yerel kameradan Mat
            // okuduğu haliyle BİREBİR aynı şekilde çalışmaya devam ediyor.
            using var mat = Cv2.ImDecode(ms.ToArray(), ImreadModes.Color);
            if (!mat.Empty())
            {
                session.UpdateFrame(mat);
            }

            if (frameCount % 50 == 1) // log'u boğmamak için her kareyi değil, her 50 karede birini yaz
            {
                logger.LogInformation("Kare #{FrameCount} işlendi: Oda={RoomId}", frameCount, roomId);
            }
        }
    }
    finally
    {
        // Bağlantı ne şekilde biterse bitsin (normal kapanma, ağ hatası, exception),
        // bu oda kayıt yapıyorsa "kamera koptu" senaryosu gibi otomatik durduruyoruz —
        // aksi halde DB'de "Recording" görünmeye devam eder ama içerik üretilmez.
        session.NotifyConnectionClosed();
    }
});

app.Run();
