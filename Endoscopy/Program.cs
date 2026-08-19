using System.Text.Json;
using System.Text.Json.Serialization;
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

// Kamera arka plan servisi: uygulama açılır açılmaz kamerayı açar
// ve sürekli en son kareyi bellekte tutar (bkz. Services/CameraService.cs).
builder.Services.AddSingleton<CameraService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraService>());

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

// --- Kayıt, kullanıcı "stop" demeden kendi kendine durursa (kamera koptu,
// disk doldu — bkz. CameraService.CaptureLoop) bunu burada DB'ye yansıtıyoruz.
// Normal "stop" akışından (CapturesController) farklı olarak burada bir HTTP
// isteği yok, o yüzden DB güncellemesi doğrudan event handler'da yapılıyor.
// Event her tetiklendiğinde kendi (kısa ömürlü) scope'unu açıyor — CaptureDbService
// artık scoped olduğu için tek bir örneği uzun süre elde tutmak doğru olmaz.
app.Services.GetRequiredService<CameraService>().RecordingAutoStopped += info =>
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CaptureDbService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    var active = db.GetActiveRecording();
    if (active == null)
    {
        logger.LogWarning("RecordingAutoStopped tetiklendi ama DB'de eşleşen 'Recording' satırı bulunamadı.");
        return;
    }

    var endedAt = DateTimeOffset.UtcNow;
    var durationMs = (long)(endedAt - active.CapturedAt).TotalMilliseconds;

    // Otomatik durma her zaman Interrupted — kullanıcının normal "stop"
    // akışından ayırt edilsin diye. Dosyanın oynatılabilir olup olmadığı
    // ayrıca log'da duruyor (bkz. CameraService), Status'u karmaşıklaştırmıyoruz.
    db.CompleteVideoCapture(active.Id, endedAt, durationMs, CaptureStatus.Interrupted, info.Width, info.Height, info.FrameCount, info.FileSizeBytes);

    logger.LogWarning(
        "Video kaydı otomatik durduruldu ve DB'de 'Interrupted' işaretlendi: Id={Id}, Sebep={Reason}, Doğrulandı={Verified}",
        active.Id, info.Reason, info.IsPlaybackVerified);
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

app.Run();
