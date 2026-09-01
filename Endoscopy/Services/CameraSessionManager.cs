using System.Collections.Concurrent;

namespace Endoscopy.Services;

/// <summary>
/// Sunucuya bağlı tüm oda/kamera oturumlarını (her biri bir WebSocket bağlantısına
/// karşılık gelir) tutan singleton kayıt defteri. Eski CameraService'in "tek kamera"
/// varsayımının yerini alıyor: artık N farklı oda, kendi bağımsız CameraSession'ına
/// sahip; hepsi bu ConcurrentDictionary üzerinden erişiliyor.
///
/// ConcurrentDictionary kullanmamızın sebebi: birden fazla WebSocket bağlantısı
/// (farklı odalar) aynı anda bağlanıp GetOrCreate çağırabilir — bu .NET'in kendi
/// thread-safe koleksiyonu olduğu için, "iki oda aynı anda ilk kez bağlanırsa ne
/// olur" sorusunu bizim ayrıca bir lock yazmamıza gerek kalmadan çözüyor.
/// </summary>
public class CameraSessionManager
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, CameraSession> _sessions = new();

    /// <summary>
    /// Herhangi bir odanın kaydı kullanıcı isteği OLMADAN durduğunda tetiklenir
    /// (bkz. CameraSession.NotifyConnectionClosed / disk doldu senaryosu).
    /// Program.cs bunu dinleyip DB'yi günceller.
    /// </summary>
    public event Action<VideoAutoStopInfo>? RecordingAutoStopped;

    public CameraSessionManager(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>Bu oda için oturum varsa döner, yoksa oluşturur (thread-safe).</summary>
    public CameraSession GetOrCreate(string roomId)
    {
        return _sessions.GetOrAdd(roomId, id =>
            new CameraSession(id, _loggerFactory.CreateLogger($"CameraSession[{id}]"), info => RecordingAutoStopped?.Invoke(info)));
    }

    /// <summary>Bu oda hiç bağlanmadıysa null döner (yeni oturum OLUŞTURMAZ — sadece bakar).</summary>
    public CameraSession? TryGet(string roomId) => _sessions.TryGetValue(roomId, out var session) ? session : null;

    /// <summary>Şu ana kadar en az bir kere bağlanmış tüm oda ID'leri.</summary>
    public IReadOnlyCollection<string> GetKnownRoomIds() => _sessions.Keys.ToList();
}
