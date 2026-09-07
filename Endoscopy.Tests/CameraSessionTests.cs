using Endoscopy.Services;
using Microsoft.Extensions.Logging;
using Moq;
using OpenCvSharp;

namespace Endoscopy.Tests;

/// <summary>
/// CameraSession sınıfının davranışlarını test eder.
///
/// Temel kural: OpenCvSharp'ın Mat ve VideoWriter sınıfları mock'lanamaz
/// (somut sınıflar), bu yüzden gerçek ama boş/gri Mat nesneleri kullanıyoruz.
/// Disk I/O gerektiren (StartRecording) testler yalnızca state değişikliklerini
/// doğrular; codec yoksa StartRecording null döner — bu da test edilen bir davranış.
/// </summary>
public class CameraSessionTests
{
    // -----------------------------------------------------------------------
    // Yardımcı fabrika — her testte tekrar yazmamak için
    // -----------------------------------------------------------------------

    /// <summary>
    /// Yeni bir CameraSession örneği oluşturur.
    /// onAutoStop: bağlantı kopma/disk dolma gibi durumlarda tetiklenen callback;
    /// null geçilirse hiçbir şey yapmayan boş bir lambda kullanılır.
    /// </summary>
    private static CameraSession CreateSession(
        string roomId = "test-room",
        Action<VideoAutoStopInfo>? onAutoStop = null)
    {
        var logger = new Mock<ILogger>().Object;
        return new CameraSession(roomId, logger, onAutoStop ?? (_ => { }));
    }

    // -----------------------------------------------------------------------
    // IsLive — son kare zamanına göre "kamera canlı mı?" sorusu
    // -----------------------------------------------------------------------

    [Fact]
    public void IsLive_HicFrameGelmemisse_FalseOlmali()
    {
        // Hiç UpdateFrame çağrılmadığında LastFrameAt = MinValue,
        // dolayısıyla IsLive false olmalı.
        var session = CreateSession();

        Assert.False(session.IsLive);
    }

    [Fact]
    public void IsLive_YeniFrameGeldiktenSonra_TrueOlmali()
    {
        // UpdateFrame çağrıldıktan hemen sonra IsLive true olmalı.
        var session = CreateSession();
        using var frame = new Mat(new Size(640, 480), MatType.CV_8UC3, Scalar.All(128));

        session.UpdateFrame(frame);

        Assert.True(session.IsLive);
    }

    [Fact]
    public void IsLive_SonFramedenSonraEsikSureGecince_FalseOlmali()
    {
        // LiveThreshold = 5 saniye. LastFrameAt'i 6 saniye geride ayarlayarak
        // eşiği aşmış bir durum simüle ediyoruz.
        var session = CreateSession();
        using var frame = new Mat(new Size(640, 480), MatType.CV_8UC3, Scalar.All(128));
        session.UpdateFrame(frame);

        // Reflection ile LastFrameAt'i 6 saniye geriye çekiyoruz.
        var prop = typeof(CameraSession).GetProperty(
            "LastFrameAt",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        prop.SetValue(session, DateTimeOffset.UtcNow.AddSeconds(-6));

        Assert.False(session.IsLive);
    }

    // -----------------------------------------------------------------------
    // IsRecording — başlangıç durumu
    // -----------------------------------------------------------------------

    [Fact]
    public void IsRecording_BaslangicDurumunda_FalseOlmali()
    {
        var session = CreateSession();

        Assert.False(session.IsRecording);
    }

    // -----------------------------------------------------------------------
    // StartRecording — kayıt başlatma
    // -----------------------------------------------------------------------

    [Fact]
    public void StartRecording_HicFrameGelmemisse_NullDonmeli()
    {
        // Kamera henüz hiç bağlanmadıysa (frame yok), kayıt başlatılamaz.
        var session = CreateSession();
        var storageDir = Path.GetTempPath();
        var codec = ("mp4v", "mp4", false);

        var result = session.StartRecording(storageDir, "test_video", codec);

        Assert.Null(result);
        Assert.NotNull(session.LastStartFailureReason);
    }

    [Fact]
    public void StartRecording_ZatenKaydediyorsa_NullDonmeli()
    {
        // Kayıt açıkken ikinci kez StartRecording çağrılırsa null dönmeli.
        var session = CreateSession();
        var storageDir = Path.GetTempPath();
        var codec = ("mp4v", "mp4", false);

        using var frame = new Mat(new Size(640, 480), MatType.CV_8UC3, Scalar.All(128));
        session.UpdateFrame(frame);

        // İlk çağrı: null dönebilir (codec yoksa) ama _isRecording bayrağı
        // sadece başarılı açılışta true olur. Durumu mock'lamak yerine
        // doğrudan _isRecording'i reflection ile set ediyoruz.
        var isRecField = typeof(CameraSession).GetField(
            "_isRecording",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        isRecField.SetValue(session, true);

        var result = session.StartRecording(storageDir, "test_video2", codec);

        Assert.Null(result);
        Assert.NotNull(session.LastStartFailureReason);
    }

    // -----------------------------------------------------------------------
    // StopRecording — kayıt durdurma
    // -----------------------------------------------------------------------

    [Fact]
    public void StopRecording_KayitYokken_NullDonmeli()
    {
        // Hiç kayıt başlatılmadan StopRecording çağrılırsa null dönmeli.
        var session = CreateSession();

        var result = session.StopRecording();

        Assert.Null(result);
    }

    // -----------------------------------------------------------------------
    // NotifyConnectionClosed — WebSocket bağlantısı kapanınca
    // -----------------------------------------------------------------------

    [Fact]
    public void NotifyConnectionClosed_KayitYokken_OnAutoStopTetiklenmemeli()
    {
        // Kayıt açık değilken bağlantı kapandığında callback çağrılmamalı.
        var callbackInvoked = false;
        var session = CreateSession(onAutoStop: _ => callbackInvoked = true);

        session.NotifyConnectionClosed();

        Assert.False(callbackInvoked);
    }

    [Fact]
    public void NotifyConnectionClosed_KaydAcikken_OnAutoStopTetiklenmeli()
    {
        // Kayıt açıkken bağlantı kapandığında callback çağrılmalı
        // ve doğru roomId ile gelmelidir.
        const string roomId = "oda-42";
        VideoAutoStopInfo? receivedInfo = null;
        var session = CreateSession(roomId, info => receivedInfo = info);

        // _isRecording'i reflection ile true yapıyoruz (gerçek VideoWriter olmadan).
        var isRecField = typeof(CameraSession).GetField(
            "_isRecording",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        isRecField.SetValue(session, true);

        // _currentFilePath de set edilmeli, yoksa CloseCurrentRecordingFile null döner.
        var filePathField = typeof(CameraSession).GetField(
            "_currentFilePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        filePathField.SetValue(session, Path.Combine(Path.GetTempPath(), "fake_recording.mp4"));

        session.NotifyConnectionClosed();

        Assert.NotNull(receivedInfo);
        Assert.Equal(roomId, receivedInfo!.RoomId);
    }

    // -----------------------------------------------------------------------
    // GetLatestFrameJpeg / GetLatestFrameClone
    // -----------------------------------------------------------------------

    [Fact]
    public void GetLatestFrameJpeg_FrameYoksa_NullDonmeli()
    {
        var session = CreateSession();

        var result = session.GetLatestFrameJpeg();

        Assert.Null(result);
    }

    [Fact]
    public void GetLatestFrameClone_FrameYoksa_NullDonmeli()
    {
        var session = CreateSession();

        var result = session.GetLatestFrameClone();

        Assert.Null(result);
    }

    [Fact]
    public void GetLatestFrameJpeg_FrameVarsa_DoluByteArrayDonmeli()
    {
        var session = CreateSession();
        using var frame = new Mat(new Size(320, 240), MatType.CV_8UC3, Scalar.All(100));
        session.UpdateFrame(frame);

        var result = session.GetLatestFrameJpeg();

        Assert.NotNull(result);
        Assert.True(result!.Length > 0);
    }

    [Fact]
    public void GetLatestFrameClone_FrameVarsa_DoluMatDonmeli()
    {
        var session = CreateSession();
        using var frame = new Mat(new Size(320, 240), MatType.CV_8UC3, Scalar.All(200));
        session.UpdateFrame(frame);

        using var result = session.GetLatestFrameClone();

        Assert.NotNull(result);
        Assert.False(result!.Empty());
        Assert.Equal(320, result.Width);
        Assert.Equal(240, result.Height);
    }

    [Fact]
    public void UpdateFrame_SonrakiCagrilar_LastFrameAtGuncellenmeli()
    {
        // Her UpdateFrame çağrısı LastFrameAt'i güncellemelidir.
        var session = CreateSession();
        using var frame = new Mat(new Size(320, 240), MatType.CV_8UC3, Scalar.All(0));

        session.UpdateFrame(frame);
        var first = session.LastFrameAt;

        // Kısa bir bekleme sonrası ikinci kare
        System.Threading.Thread.Sleep(10);
        session.UpdateFrame(frame);
        var second = session.LastFrameAt;

        Assert.True(second >= first);
    }
}
