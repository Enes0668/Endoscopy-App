using TagLib;
using TagLib.Mpeg4;
using TagLib.Xmp;

namespace Endoscopy.Services;

/// <summary>
/// Yakalanan JPEG/MP4 dosyasının KENDİ İÇİNE, DB'den bağımsız olarak, hangi
/// istasyondan (makine adı/IP/MAC) geldiğini yazan yardımcı. Bkz. konuşma:
/// dosya paylaşımlı diske kopyalanır/yedeklenirse DB'den kopabilir — bu
/// tag'ler dosyanın kendi kendini kanıtlamasını (self-contained provenance)
/// sağlar.
///
/// Genel amaçlı alanlara (Comment/Description) YAZMIYORUZ — Furkan'ın
/// belirttiği gibi ("IsDeleted'ı oraya koymazsın, yeri orası değil") her
/// bilginin ait olduğu yer var:
///   - JPEG: standart tag'ler yerine kendi custom XMP namespace'imiz.
///   - MP4: standart bir atom yok; TagLibSharp'ın freeform ("----" / dash box)
///     mekanizmasıyla, kendi "mean" tanımlayıcımız altında custom atomlar.
/// </summary>
public static class FileIdentityTagger
{
    private const string XmpNamespace = "https://endoscopy.local/ns/capture/1.0/";
    private const string Mp4Mean = "local.endoscopy.capture";

    /// <summary>
    /// Dosyayı (jpg ya da mp4) açar, capture'ı üreten istasyonun kimliğini
    /// yazar ve kaydeder. Format XMP'yi (jpg) ya da Apple/freeform atomları
    /// (mp4) desteklemiyorsa ilgili adım sessizce atlanır. Hata durumunda
    /// (dosya kilitli, format tanınmıyor vb.) false döner ve loglanır — bu bir
    /// zenginleştirme adımı, capture akışını başarısız kılacak kritik bir
    /// adım DEĞİL (DB'deki asıl kayıt zaten bu bilgiyi taşıyor).
    /// </summary>
    public static bool TryWriteCaptureIdentity(string physicalPath, string machineName, string? ipAddress, string? macAddress, ILogger logger)
    {
        try
        {
            using var file = TagLib.File.Create(physicalPath);
            var wroteAnything = false;

            // JPEG (ve XMP destekleyen diğer formatlar): custom namespace.
            if (file.GetTag(TagTypes.XMP, true) is XmpTag xmp)
            {
                xmp.SetTextNode(XmpNamespace, "MachineName", machineName);
                if (ipAddress != null) xmp.SetTextNode(XmpNamespace, "LocalIpAddress", ipAddress);
                if (macAddress != null) xmp.SetTextNode(XmpNamespace, "LocalMacAddress", macAddress);
                wroteAnything = true;
            }

            // MP4: standart bir "capture device" atomu yok, freeform ("----")
            // dash box kullanıyoruz — iTunes'un custom metadata'sıyla aynı mekanizma.
            if (file.GetTag(TagTypes.Apple, true) is AppleTag apple)
            {
                apple.SetDashBox(Mp4Mean, "MachineName", machineName);
                if (ipAddress != null) apple.SetDashBox(Mp4Mean, "LocalIpAddress", ipAddress);
                if (macAddress != null) apple.SetDashBox(Mp4Mean, "LocalMacAddress", macAddress);
                wroteAnything = true;
            }

            if (!wroteAnything)
            {
                logger.LogWarning("Dosya formatı ne XMP ne Apple/freeform atom destekliyor, capture kimliği yazılamadı: {Path}", physicalPath);
                return false;
            }

            file.Save();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dosyaya capture kimliği (machine/IP/MAC) yazılamadı: {Path}", physicalPath);
            return false;
        }
    }
}
