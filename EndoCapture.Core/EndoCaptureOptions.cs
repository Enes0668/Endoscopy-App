namespace Endoscopy;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddEndoCapture"/> ile yapılandırılan
/// seçenekler. Bağlantı dizesi ve depolama yolu zorunlu; diğerleri isteğe bağlı.
/// </summary>
public class EndoCaptureOptions
{
    /// <summary>
    /// PostgreSQL bağlantı dizesi.
    /// Örn: "Host=localhost;Port=5432;Database=appdb;Username=postgres;Password=..."
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Fotoğraf ve video dosyalarının kaydedileceği klasör yolu.
    /// Göreli yol verilirse, uygulamanın ContentRootPath'ine göre çözümlenir.
    /// Varsayılan: "storage"
    /// </summary>
    public string StoragePath { get; set; } = "storage";
}
