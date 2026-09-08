namespace Endoscopy.Models;

/// <summary>Bir yakalamanın türü. JSON'da camelCase serileştiği için (bkz. Program.cs)
/// "Photo" -&gt; "photo", "Video" -&gt; "video" olarak gider — API sözleşmesi değişmedi.</summary>
public enum CaptureType
{
    Photo,
    Video
}

/// <summary>
/// Bir yakalamanın (özellikle videonun) durumu:
/// - Recording: video hâlâ kaydediliyor
/// - Completed: kullanıcı normal şekilde durdurdu, dosya doğrulandı (fotoğrafta hep bu)
/// - Corrupted: normal şekilde durduruldu ama dosya geri okunamadı/bozuk çıktı
/// - Interrupted: kullanıcı "stop" demeden bitti (çökme, kamera koptu, disk doldu)
/// </summary>
public enum CaptureStatus
{
    Recording,
    Completed,
    Corrupted,
    Interrupted
}
