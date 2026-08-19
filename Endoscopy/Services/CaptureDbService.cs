using Endoscopy.Data;
using Endoscopy.Models;
using Microsoft.EntityFrameworkCore;

namespace Endoscopy.Services;

/// <summary>
/// "/api/captures" listesinde ve durum sorgularında dönen düz (flat) okuma
/// modeli. MediaCapture entity'si write tarafında (EF Core ile ekleme/güncelleme)
/// kullanılıyor; bu kayıt ise sadece okuma/JSON çıktısı için.
/// </summary>
public record CaptureListItem(
    long Id,
    CaptureType CaptureType,
    string FilePath,
    DateTimeOffset CapturedAt,
    DateTimeOffset? EndedAt,
    long? DurationMs,
    CaptureStatus Status,
    string TriggerSource,
    string? PatientIdentifier,
    string? PatientName,
    string? DoctorName,
    string? ProcedureType,
    string? RoomName,
    int? Width,
    int? Height,
    long? FrameCount,
    long? FileSizeBytes,
    string? CreatedByName);

/// <summary>Bir capture oluştururken "kime/hangi işleme ait" bilgisini taşıyan opsiyonel paket.</summary>
public record CaptureContext(
    string? PatientIdentifier = null,
    string? PatientName = null,
    string? DoctorName = null,
    string? ProcedureType = null,
    string? RoomName = null,
    string? CreatedByName = null);

/// <summary>
/// PostgreSQL üzerinde EF Core ile çalışan veri erişim katmanı. Şema artık
/// Models/ altındaki gerçek sınıflardan ve EF Core migration'larından geliyor
/// (bkz. AppDbContext); burada elle yazılmış SQL/ALTER TABLE yok.
/// </summary>
public class CaptureDbService
{
    private readonly AppDbContext _db;
    private readonly ILogger<CaptureDbService> _logger;

    public CaptureDbService(AppDbContext db, ILogger<CaptureDbService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Yeni bir capture satırı ekler. Fotoğrafta status daima Completed, videoda
    /// Recording ile başlar. "context" opsiyonel — arayüzden hasta/doktor/prosedür
    /// bilgisi girilmediyse null geçilebilir, alanlar boş kalır (zorunlu değil).
    /// "fileSizeBytes" fotoğrafta hemen biliniyor (dosya o an tam yazıldı); videoda
    /// null geçilir, kayıt bitince CompleteVideoCapture ile doldurulur.
    /// </summary>
    public long InsertCapture(CaptureType captureType, string filePath, DateTimeOffset capturedAt, string triggerSource, CaptureStatus status = CaptureStatus.Completed, CaptureContext? context = null, long? fileSizeBytes = null)
    {
        var entity = new MediaCapture
        {
            CaptureType = captureType,
            FilePath = filePath,
            CapturedAt = capturedAt,
            TriggerSource = triggerSource,
            Status = status,
            PatientIdentifier = context?.PatientIdentifier,
            PatientName = context?.PatientName,
            DoctorName = context?.DoctorName,
            ProcedureType = context?.ProcedureType,
            RoomName = context?.RoomName,
            FileSizeBytes = fileSizeBytes,
            CreatedByName = context?.CreatedByName
        };

        _db.MediaCaptures.Add(entity);
        _db.SaveChanges();
        return entity.Id;
    }

    /// <summary>
    /// Devam eden video kaydını bitmiş olarak işaretler. "status" normalde
    /// Completed (dosya doğrulandı) ya da Corrupted (dosya geri okunamadı) olur.
    /// Width/Height/FrameCount/FileSizeBytes opsiyonel — otomatik durma senaryolarında
    /// bile (CameraService artık her zaman ölçüp döndürüyor) doldurulabilir olmalı,
    /// ama null geçilirse mevcut değer değişmeden kalır (bilinmiyorsa ezmiyoruz).
    /// </summary>
    public void CompleteVideoCapture(long id, DateTimeOffset endedAt, long durationMs, CaptureStatus status = CaptureStatus.Completed, int? width = null, int? height = null, long? frameCount = null, long? fileSizeBytes = null)
    {
        var entity = _db.MediaCaptures.Find(id);
        if (entity == null)
        {
            _logger.LogWarning("CompleteVideoCapture: Id={Id} bulunamadı.", id);
            return;
        }

        entity.EndedAt = endedAt;
        entity.DurationMs = durationMs;
        entity.Status = status;
        if (width.HasValue) entity.Width = width;
        if (height.HasValue) entity.Height = height;
        if (frameCount.HasValue) entity.FrameCount = frameCount;
        if (fileSizeBytes.HasValue) entity.FileSizeBytes = fileSizeBytes;
        _db.SaveChanges();
    }

    /// <summary>Tek bir kaydı Id'sine göre döner (entity, yazma amaçlı) — bulunamazsa null.</summary>
    public MediaCapture? GetById(long id) => _db.MediaCaptures.Find(id);

    /// <summary>
    /// "Yenile" butonu için: dosyadan yeniden okunan Width/Height/FrameCount/
    /// FileSizeBytes ile mevcut satırı üzerine yazar (backfill / manuel düzeltme).
    /// Null geçilen alan dokunulmadan kalır.
    /// </summary>
    public bool UpdateFileMetadata(long id, int? width, int? height, long? frameCount, long? fileSizeBytes)
    {
        var entity = _db.MediaCaptures.Find(id);
        if (entity == null) return false;

        if (width.HasValue) entity.Width = width;
        if (height.HasValue) entity.Height = height;
        if (frameCount.HasValue) entity.FrameCount = frameCount;
        if (fileSizeBytes.HasValue) entity.FileSizeBytes = fileSizeBytes;
        _db.SaveChanges();
        return true;
    }

    /// <summary>
    /// Status=Recording olan en güncel satırı döner. Sayfa yenilendiğinde
    /// ya da sunucu yeniden başlatıldığında "hâlâ kayıt devam ediyor mu?"
    /// sorusuna bu üzerinden cevap verilir.
    /// </summary>
    public MediaCapture? GetActiveRecording()
    {
        return _db.MediaCaptures
            .Where(c => c.Status == CaptureStatus.Recording)
            .OrderByDescending(c => c.CapturedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Aktif (silinmemiş) kayıtları (fotoğraf + video) en yeniden eskiye döner.
    /// Soft-delete edilmiş (IsActive=false) satırlar bu listede görünmez —
    /// veri hâlâ DB'de duruyor, sadece normal listelemeden gizleniyor.
    /// </summary>
    public List<CaptureListItem> GetCaptures()
    {
        return _db.MediaCaptures
            .Where(c => c.IsActive)
            .OrderByDescending(c => c.CapturedAt)
            .Select(c => new CaptureListItem(
                c.Id,
                c.CaptureType,
                c.FilePath,
                c.CapturedAt,
                c.EndedAt,
                c.DurationMs,
                c.Status,
                c.TriggerSource,
                c.PatientIdentifier,
                c.PatientName,
                c.DoctorName,
                c.ProcedureType,
                c.RoomName,
                c.Width,
                c.Height,
                c.FrameCount,
                c.FileSizeBytes,
                c.CreatedByName))
            .ToList();
    }

    /// <summary>
    /// Bir kaydı soft-delete eder (IsActive=false). Dosya diskten silinmez,
    /// DB satırı da kalıcı olarak kaldırılmaz — sadece normal listelemeden
    /// (GetCaptures) gizlenir. Tıbbi kayıt olduğu için kalıcı/geri dönüşsüz
    /// silme bilerek yapılmıyor. Bulunamazsa false döner.
    /// </summary>
    public bool DeactivateCapture(long id)
    {
        var entity = _db.MediaCaptures.Find(id);
        if (entity == null) return false;

        entity.IsActive = false;
        _db.SaveChanges();
        _logger.LogInformation("Capture soft-delete edildi: Id={Id}", id);
        return true;
    }

    /// <summary>
    /// Uygulama açılışında bir kere çağrılır (bkz. Program.cs). Önceki çalıştırmadan
    /// kalma, hâlâ Status=Recording görünen satırlar varsa (uygulama çökmüş ya da
    /// zorla kapatılmış demektir — bellekteki gerçek kayıt durumu her zaman sıfırdan
    /// başlar) bunları Interrupted olarak işaretler. Bitiş zamanı olarak, dosyanın
    /// diskteki son yazılma zamanını (gerçek son başarılı kare yazımını) kullanır;
    /// bu, "ne zaman durdu" için elimizdeki en doğru tahmin.
    /// </summary>
    public int ReconcileStaleRecordings(string contentRootPath)
    {
        var staleRows = _db.MediaCaptures
            .Where(c => c.Status == CaptureStatus.Recording)
            .ToList();

        foreach (var capture in staleRows)
        {
            var physicalPath = Path.Combine(contentRootPath, capture.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

            DateTimeOffset endedAt;
            try
            {
                endedAt = File.Exists(physicalPath)
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(physicalPath), TimeSpan.Zero)
                    : DateTimeOffset.UtcNow;
            }
            catch
            {
                endedAt = DateTimeOffset.UtcNow;
            }

            if (endedAt < capture.CapturedAt) endedAt = capture.CapturedAt; // negatif süreyi engelle

            capture.Status = CaptureStatus.Interrupted;
            capture.EndedAt = endedAt;
            capture.DurationMs = (long)(endedAt - capture.CapturedAt).TotalMilliseconds;

            try
            {
                if (File.Exists(physicalPath)) capture.FileSizeBytes = new FileInfo(physicalPath).Length;
            }
            catch { /* boyut okunamazsa önemli değil, kritik bilgi değil */ }
        }

        if (staleRows.Count > 0)
        {
            _db.SaveChanges();
            _logger.LogWarning(
                "Uygulama başlangıcında {Count} adet yarım kalmış (önceki çalıştırmadan kalma) video kaydı 'Interrupted' olarak işaretlendi.",
                staleRows.Count);
        }

        return staleRows.Count;
    }
}
