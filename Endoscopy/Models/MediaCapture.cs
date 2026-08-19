namespace Endoscopy.Models;

/// <summary>
/// Bir fotoğraf ya da video yakalamasını temsil eden ana tablo. Video dosyaları
/// artık ortak/paylaşımlı diskte tutuluyor — bu satır o dosyaya bir REFERANS
/// (FilePath) ve dosyanın "kime, hangi işleme ait" olduğu bilgisini taşıyor;
/// dosyanın byte'ları burada değil.
/// </summary>
public class MediaCapture
{
    public long Id { get; set; }

    public CaptureType CaptureType { get; set; }

    /// <summary>Dosyanın ortak diskteki/yerel storage'daki yolu (fiziksel byte'lar burada DEĞİL, sadece referans).</summary>
    public string FilePath { get; set; } = string.Empty;

    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>Sadece video için doldurulur; kayıt sürerken null.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Sadece video için doldurulur.</summary>
    public long? DurationMs { get; set; }

    public CaptureStatus Status { get; set; } = CaptureStatus.Completed;

    /// <summary>Neyin tetiklediği (WEB_BUTTON, PEDAL_CLICK vb. — gerçek cihazda seri porttan gelecek).</summary>
    public string TriggerSource { get; set; } = string.Empty;

    // --- "Bu kayıt kime/hangi işleme ait" bilgisi ---
    // Şu an serbest metin: hastanenin HIS/EHR sistemiyle entegrasyon henüz
    // netleşmedi (bkz. konuşma), o yüzden burada gerçek bir Patient/Doctor
    // tablosuna foreign key vermek yerine düz alanlar kullanıyoruz. Entegrasyon
    // netleşince PatientIdentifier gerçek bir hasta ID'sine, DoctorName da
    // gerçek bir doktor kaydına referans haline getirilebilir.
    // Hepsi nullable: geriye dönük kayıtlarda (bu alanlar eklenmeden önce
    // oluşturulmuş satırlarda) veri yok, yeni kayıtlarda da arayüzden
    // doldurulmazsa boş kalabilir — zorunlu tutmuyoruz.

    /// <summary>Hastanenin kendi sisteminde kullandığı hasta no/MRN (varsa).</summary>
    public string? PatientIdentifier { get; set; }

    public string? PatientName { get; set; }

    /// <summary>Prosedürü gerçekleştiren doktor.</summary>
    public string? DoctorName { get; set; }

    /// <summary>Örn. "Kolonoskopi", "Gastroskopi" — sabit bir liste olarak zorlamıyoruz, kurumdan kuruma değişebilir.</summary>
    public string? ProcedureType { get; set; }

    /// <summary>Kaydın yapıldığı oda/istasyon — birden fazla kayıt istasyonu olduğunda hangi odadan geldiğini ayırt eder.</summary>
    public string? RoomName { get; set; }

    // --- Video'ya özgü teknik bilgi ---
    // Sadece video için doldurulur (fotoğrafta null kalır). Kayıt kamerasının
    // gerçek çözünürlüğü/kare üretimi zamanla değişebilir (kamera değişir,
    // ayar değişir) — bunu her kayıtta ayrı ayrı tutmak, geriye dönük "bu kayıt
    // hangi kalitede/hızda çekilmiş" sorusuna dosyayı açmadan cevap verir.

    /// <summary>Kayıt karesinin genişliği (piksel). Sadece video.</summary>
    public int? Width { get; set; }

    /// <summary>Kayıt karesinin yüksekliği (piksel). Sadece video.</summary>
    public int? Height { get; set; }

    /// <summary>
    /// Gerçekten diske yazılan kare sayısı — VideoWriter'a verilen "deklare edilen"
    /// fps'ten (şu an sabit 20) farklı olabilir, çünkü kameranın gerçek üretim hızı
    /// o değere tam uymayabilir. Gerçek ortalama fps = FrameCount / (DurationMs/1000)
    /// ile hesaplanabilir; DB'de ayrıca saklamıyoruz, türetilmiş bir değer.
    /// </summary>
    public long? FrameCount { get; set; }

    /// <summary>
    /// Dosyanın diskteki gerçek boyutu (byte). Hem fotoğraf hem video için doldurulur
    /// (fotoğrafta kayıt anında, videoda dosya kapanınca — çünkü video sürerken boyut
    /// hâlâ büyüyor olur). Disk doluluğu takibiyle ilişkili: hangi kayıtların ne kadar
    /// yer kapladığını dosyayı tek tek açmadan görmek için.
    /// </summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// Soft-delete bayrağı: false olursa bu kayıt "silinmiş" sayılır ama
    /// fiziksel olarak veritabanından kaldırılmaz — tıbbi kayıt olduğu için
    /// gerçek silme riskli (yanlışlıkla ya da kötü niyetle kalıcı kayıp
    /// olmasın diye). Normal listeleme (GetCaptures) bu satırları göstermez,
    /// ama veri hâlâ DB'de duruyor, gerekirse geri getirilebilir.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Bu kaydı kimin oluşturduğu — şimdilik geçici, serbest metin bir alan.
    /// Sistemde henüz giriş/kimlik doğrulama olmadığı için gerçek bir kullanıcı
    /// hesabına referans veremiyoruz (TriggerSource sadece "neyle" tetiklendiğini
    /// söylüyor, "kim" olduğunu değil); kimlik doğrulama eklenince bu alan
    /// gerçek bir kullanıcı ID'sine dönüştürülebilir.
    /// </summary>
    public string? CreatedByName { get; set; }
}
