# EndoCapture — Endoskopi Kayıt Sistemi

Merkezi bir sunucu, birden fazla odadaki (istasyondaki) endoskopi kameralarından
tarayıcı üzerinden (WebSocket ile) canlı görüntü alıp fotoğraf/video olarak
kaydeden, PostgreSQL veritabanına yazan bir ASP.NET Core uygulaması.

## Mimari özet

- **Kamera, sunucuda değil, her odanın tarayıcısında açılıyor** (`getUserMedia`).
  Tarayıcı, kameradan aldığı kareleri WebSocket üzerinden (`/ws-camera/{roomId}`)
  sunucuya gönderir.
- Sunucu, her oda için ayrı ve birbirinden izole bir `CameraSession` tutar
  (`Services/CameraSession.cs`) — bir odanın kaydı/görüntüsü başka bir odayı
  hiç etkilemez.
- **Özel odalarda birden fazla kamera desteklenir** (tek bilgisayara bağlı 2+
  fiziksel kamera, örn. ana kamera + yardımcı kamera). Sayfa açılışında
  `enumerateDevices()` ile bulunan her kamera kendi alt-oda kimliğini alır
  (`oda1-cam1`, `oda1-cam2` gibi) ve tamamen bağımsız çalışır — biri kayıt
  yaparken diğeri fotoğraf çekebilir. Tek kameralı normal odalarda davranış
  değişmez (düz `oda1`).
- Fotoğraf/video kayıtları `MediaCaptures` tablosuna (PostgreSQL, EF Core ile)
  yazılır; hangi odadan geldiği `RoomName` alanıyla ayırt edilir.
- Kimlik doğrulama (login) **yok** — oda ekranı, kendi oda kimliğini serbestçe
  girer. Bu bilinçli bir tercih (bkz. "Bilinen kısıtlar").

## Gereksinimler

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- PostgreSQL (yerelde ya da erişilebilir bir sunucuda)
- **Windows** — `OpenCvSharp4.runtime.win` paketi kullanıldığı için şu an sadece
  Windows'ta çalışıyor (Linux/Mac desteği için ayrı bir runtime paketi gerekir).
- Bir web kamerası (fotoğraf/video akışını test etmek için) — bu, sunucunun
  değil, uygulamayı açan **her tarayıcının kendi** kamerası.

## Kurulum

1. Depoyu klonla:
   ```bash
   git clone https://github.com/Enes0668/Endoscopy-App.git
   cd Endoscopy-App
   ```

2. PostgreSQL bağlantı bilgisini ayarla — `Endoscopy/appsettings.Development.json`
   içindeki `ConnectionStrings:DefaultConnection` değerini kendi PostgreSQL
   kurulumuna göre düzenle:
   ```json
   "ConnectionStrings": {
     "DefaultConnection": "Host=localhost;Port=5432;Database=appdb;Username=postgres;Password=..."
   }
   ```
   Veritabanının (`appdb`) kendisini elle oluşturmana gerek yok — uygulama
   ilk açılışta EF Core migration'larını otomatik uygular (bkz. `Program.cs`,
   `dbContext.Database.Migrate()`), tablolar kendiliğinden oluşur.

3. Çalıştır:
   ```bash
   dotnet run --project Endoscopy
   ```
   Terminalde `"Now listening on: http://0.0.0.0:5199"` satırını görünce hazırdır.

4. Tarayıcıda aç: `http://localhost:5199/`

## Testleri Çalıştırma

Proje, `Endoscopy.Tests` adlı bir xUnit test projesi içerir. Toplam **75 test**
aşağıdaki alanları kapsar:

| Test dosyası | Test | Kapsam |
|---|---|---|
| `CameraSessionTests.cs` | 11 | IsLive, IsRecording, StartRecording, StopRecording, bağlantı kopma, frame alma |
| `CameraSessionManagerTests.cs` | 8 | Oturum yönetimi, GetOrCreate, TryGet, RecordingAutoStopped event |
| `CaptureDbServiceTests.cs` | 27 | Insert, Complete, GetById, filtreleme, soft-delete, çok kameralı oda, ReconcileStaleRecordings |
| `CapturesControllerTests.cs` | 14 | API endpoint'leri (HTTP 200/400/404/409/500) |
| `AutoStopChainTests.cs` | 4 | Bağlantı kopunca DB'ye Interrupted yazılması |
| `CodecDetectorTests.cs` | 5 | Codec önbellek, FOURCC/extension doğruluğu |
| `FileIdentityTaggerTests.cs` | 4 | JPEG/dosyaya kimlik yazma, hata toleransı |

Tümünü çalıştırmak için:

```bash
dotnet test
```

## Sayfalar

| Adres | Ne işe yarar |
|---|---|
| `/` (`index.html`) | Bir odanın ana ekranı — sayfa açılışında o bilgisayardaki **tüm kameraları otomatik bulur** (dahili webcam, ikinci bir USB kamera, OBS sanal kamera vb.), her biri için ayrı bir kart (canlı önizleme + foto/video kontrolü) açar, WebSocket ile gönderir, **sadece kendi odasının** (ve varsa alt-kameralarının) kayıtlarını listeler. |
| `/admin.html` | **Tüm odaların** kayıtlarını (filtresiz, ya da istersen tek bir odaya daraltarak) gösteren yönetici görünümü. |
| `/camera-ws-test.html` | WebSocket/kamera akışını izole test etmek için bağımsız test sayfası. |
| `/multi-camera-test.html` | Çoklu kamera senaryosunu (manuel seçim ile) test etmek için ayrı sayfa. |

## Farklı bir bilgisayardan/telefondan erişme

Uygulama `0.0.0.0`'a bağlı olduğu için, aynı ağdaki (Wi-Fi/LAN) başka
cihazlardan da erişilebilir:

1. Bu bilgisayarın yerel IP adresini öğren (PowerShell: `ipconfig`, "IPv4
   Address" satırına bak — örn. `192.168.1.185`).
2. **Önemli:** Kamera erişimi (`getUserMedia`), tarayıcı güvenlik politikası
   gereği sadece `https://` ya da `localhost` üzerinden çalışır. Düz
   `http://<ip>:5199` ile açarsan sayfa yüklenir ama **kamera izni hiç
   sorulmaz.** Bunun yerine `https` profiliyle çalıştır:
   ```bash
   dotnet run --project Endoscopy --launch-profile https
   ```
   ve diğer cihazdan `https://<ip>:7297` adresine git.
3. Tarayıcı, geliştirme sertifikası bu cihazda güvenilir olmadığı için bir
   **"Bağlantınız güvenli değil"** uyarısı gösterecek — "Gelişmiş" →
   "Yine de devam et" ile geçilebilir (test için kabul edilebilir; gerçek
   kullanım için geçerli bir SSL sertifikası gerekir, bkz. "Bilinen kısıtlar").
4. İlk bağlantıda Windows Güvenlik Duvarı bir izin penceresi çıkarabilir —
   "İzin Ver" de.

Bu, telefon + bilgisayardan eşzamanlı, bağımsız kayıt alınarak test edildi.

## API kısa özeti

Oda bazlı (hepsi `{roomId}` alır, örn. `oda1`):
- `POST /api/rooms/{roomId}/capture` — fotoğraf çeker
- `POST /api/rooms/{roomId}/capture/video/start` / `.../stop` — video kaydı
- `GET /api/rooms/{roomId}/capture/video/status` — kayıt durumu
- `GET /api/rooms/{roomId}/video-feed` — canlı MJPEG önizleme

Oda'dan bağımsız:
- `GET /api/captures?roomId=...` — kayıt listesi (`roomId` verilmezse tüm odalar; verilirse o oda ile onun `-cam1`, `-cam2` gibi alt-kameralarının kayıtları birlikte döner)
- `DELETE /api/captures/{id}` — soft-delete
- `POST /api/captures/{id}/refresh-metadata` — dosyadan metadata'yı tazele

WebSocket (API değil, ham bağlantı):
- `ws(s)://.../ws-camera/{roomId}` — tarayıcının kamera karelerini bu odaya akıttığı yer

## Proje yapısı

```
Endoscopy/
├── Controllers/
│   └── CapturesController.cs     # Tüm HTTP endpoint'leri
├── Data/
│   └── AppDbContext.cs            # EF Core context
├── Models/
│   └── MediaCapture.cs            # Veritabanı entity'si
├── Services/
│   ├── CameraSession.cs           # Tek bir odanın kamera/kayıt oturumu
│   ├── CameraSessionManager.cs    # Tüm oturumların merkezi yöneticisi
│   ├── CaptureDbService.cs        # Veritabanı servis katmanı
│   ├── CodecDetector.cs           # Video codec tespiti ve önbellekleme
│   ├── DeviceIdentityService.cs   # Makine adı/IP/MAC tespiti
│   ├── FileIdentityTagger.cs      # Dosyaya kimlik yazma (TagLibSharp)
│   └── VideoFileMetadataReader.cs # Video dosyasından metadata okuma
├── wwwroot/
│   ├── index.html                 # Ana oda ekranı (dark, modern UI)
│   ├── admin.html                 # Yönetici kayıt listesi
│   ├── camera-ws-test.html        # Tekli kamera test sayfası
│   └── multi-camera-test.html     # Çoklu kamera test sayfası
└── Program.cs                     # Uygulama başlangıcı, WebSocket handler

Endoscopy.Tests/
├── CameraSessionTests.cs
├── CameraSessionManagerTests.cs
├── CaptureDbServiceTests.cs
├── CapturesControllerTests.cs
├── AutoStopChainTests.cs
├── CodecDetectorTests.cs
└── FileIdentityTaggerTests.cs
```

## Bilinen kısıtlar / sonradan ele alınması gerekenler

- **Kimlik doğrulama yok.** Herhangi biri herhangi bir oda kimliği yazıp o
  odanın verisine erişebilir/kayıt başlatabilir. Şu an bilinçli olarak böyle
  (login istenmedi), ama gerçek/çok kullanıcılı bir ortamda yeniden
  değerlendirilmeli.
- **Oda kimliği, `RoomName` serbest metin alanına yazılıyor** — ayrı bir
  `RoomId` kolonu/migration yok. Çalışıyor ama daha temiz bir çözüm ileride
  düşünülebilir.
- **Geliştirme sertifikası** başka cihazlarda güvenilir değil — gerçek
  kullanım için geçerli bir SSL sertifikası (örn. Let's Encrypt) gerekir.
- **Kamera "kopması" tespiti** sadece WebSocket bağlantısı tamamen kapanınca
  tetikleniyor; bağlantı açık ama sessiz kalırsa (örn. birkaç saniye kare
  gelmezse) otomatik durdurma yok.
- **Sadece Windows'ta çalışıyor** (`OpenCvSharp4.runtime.win` paketi yüzünden).
- Endoskopi cihazının gerçekten UVC-uyumlu olup olmadığı henüz gerçek cihazla
  doğrulanmadı — şimdiye kadarki tüm testler bilgisayar/telefon kamerasıyla yapıldı.
