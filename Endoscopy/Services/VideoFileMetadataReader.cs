using OpenCvSharp;

namespace Endoscopy.Services;

/// <summary>
/// Var olan bir video dosyasını (kayıt sürecinin dışında, elle) yeniden açıp
/// gerçek genişlik/yükseklik/kare sayısını okuyan bağımsız bir yardımcı.
/// Eskiden CameraService.ReadVideoFileMetadata idi — hiçbir oturum durumuna
/// (state) ihtiyaç duymadığı için (sadece dosyayı açıp metadata okuyor),
/// artık bağımsız, static bir sınıf. VideoCapture'ın kendi container
/// metadata'sından (moov'daki bilgiden) okunuyor — tüm kareleri tek tek
/// okumaya gerek yok, hızlı.
/// </summary>
public static class VideoFileMetadataReader
{
    public static (int Width, int Height, long FrameCount)? Read(string path)
    {
        try
        {
            using var reader = new VideoCapture(path);
            if (!reader.IsOpened()) return null;
            return (reader.FrameWidth, reader.FrameHeight, (long)reader.FrameCount);
        }
        catch
        {
            return null;
        }
    }
}
