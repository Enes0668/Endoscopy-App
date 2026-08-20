using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Endoscopy.Services;

/// <summary>
/// Bu uygulamanın çalıştığı istasyonun (bilgisayarın) kimliğini bir kere
/// tespit edip önbelleğe alır: MachineName, yerel IPv4, yerel MAC. Bu bilgi
/// hem DB'ye (bkz. MediaCapture.MachineName/LocalIpAddress/LocalMacAddress)
/// hem de üretilen dosyanın kendi metadata'sına (bkz. FileIdentityTagger)
/// yazılır — amaç, dosya DB'den koparsa (paylaşımlı diske kopyalanırsa,
/// yedeklenirse) bile hangi istasyondan geldiğinin dosyanın kendisinden de
/// anlaşılabilmesi.
///
/// Önemli ayrım: burada ağ üzerinden BAŞKA bir cihazın MAC'ini tespit etmiyoruz
/// (bu, router arkasında pratikte mümkün değil — bkz. proje özeti, ertelenen
/// konular). Sadece BU makinenin kendi aktif ağ adaptörünü okuyoruz; tamamen
/// yerel bir işlem, ağdan hiçbir şey "sorulmuyor". Codec tespitinde olduğu gibi
/// (bkz. CameraService.DetectWorkingCodec) sonuç bir kere hesaplanıp önbelleğe
/// alınır — bir istasyonun IP/MAC'i uygulama çalışırken değişmez varsayımıyla.
/// </summary>
public class DeviceIdentityService
{
    private readonly Lazy<(string MachineName, string? IpAddress, string? MacAddress)> _identity;

    public DeviceIdentityService(ILogger<DeviceIdentityService> logger)
    {
        _identity = new Lazy<(string, string?, string?)>(() => Detect(logger));
    }

    public string MachineName => _identity.Value.MachineName;
    public string? LocalIpAddress => _identity.Value.IpAddress;
    public string? LocalMacAddress => _identity.Value.MacAddress;

    private static (string MachineName, string? IpAddress, string? MacAddress) Detect(ILogger logger)
    {
        var machineName = Environment.MachineName;
        string? ipAddress = null;
        string? macAddress = null;

        try
        {
            // "Up" durumdaki, loopback olmayan, gerçekten bir IPv4 adresi taşıyan
            // ilk adaptörü baz alıyoruz. Birden fazla adaptör varsa (Wi-Fi +
            // Ethernet aynı anda aktifse) bu seçim kesin değil, ama tek istasyon
            // ölçeğinde (şu anki kapsam) pratikte yeterli.
            var candidate = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .FirstOrDefault(ni => ni.GetIPProperties().UnicastAddresses
                    .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork));

            if (candidate != null)
            {
                macAddress = string.Join(":", candidate.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                ipAddress = candidate.GetIPProperties().UnicastAddresses
                    .First(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Address.ToString();
            }
            else
            {
                logger.LogWarning("Aktif bir ağ adaptörü bulunamadı; LocalIpAddress/LocalMacAddress boş kalacak.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Yerel IP/MAC tespiti başarısız oldu; DB'de/dosyada bu alanlar boş kalacak.");
        }

        return (machineName, ipAddress, macAddress);
    }
}
