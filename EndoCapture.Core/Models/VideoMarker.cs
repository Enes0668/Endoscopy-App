namespace Endoscopy.Models;

/// <summary>
/// Bir video kaydı içerisindeki belirli bir zaman damgasına (timestamp)
/// ait klinik işaret/not (bookmark / marker).
/// </summary>
public class VideoMarker
{
    public long Id { get; set; }

    /// <summary>İlişkili MediaCapture kaydının kimliği.</summary>
    public long MediaCaptureId { get; set; }

    /// <summary>Video başlangıcından itibaren geçen süre (milisaniye).</summary>
    public long TimestampMs { get; set; }

    /// <summary>İşaretin etiketi veya açıklaması (örn. Polip, Kanama Odağı, Biyopsi, Z Çizgisi vb.).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>İşaretin oluşturulduğu an (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation property
    public MediaCapture? MediaCapture { get; set; }
}
