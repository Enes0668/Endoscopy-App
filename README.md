# Endoskopi Web Uygulaması

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

## Sayfalar

| Adres | Ne işe yarar |
|---|---|
| `/` (`index.html`) | Bir odanın ana ekranı — sayfa açılışında o bilgisayardaki **tüm kameraları otomatik bulur** (dahili webcam, ikinci bir USB kamera, OBS sanal kamera vb.), her biri için ayrı bir kart (canlı önizleme + foto/video kontrolü) açar, WebSocket ile gönderir, **sadece kendi odasının** (ve varsa alt-kameralarının) kayıtlarını listeler. Üstteki "oda kimliği" kutusuna hangi odaysan onu yaz. |
| `/admin.html` | **Tüm odaların** kayıtlarını (filtresiz, ya da istersen tek bir odaya daraltarak) gösteren yönetici görünümü. Kamera/kayıt kontrolü yok, sadece listeleme. |
| `/camera-ws-test.html` | Bağımsız bir test sayfası — sadece WebSocket/kamera akışını (tek kamera, fotoğraf/video API çağrılarıyla birlikte) izole test etmek için, ana akışın parçası değil. |
| `/multi-camera-test.html` | Çoklu kamera senaryosunu (tek bilgisayara bağlı birden fazla kamera, manuel seçim ile) izole test etmek için ayrı bir sayfa. `index.html`'deki otomatik-bulma mantığının, elle seçim yapılabilen deneysel hali. |

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

## API'nin kısa özeti

Oda bazlı (hepsi `{roomId}` alır, örn. `oda1`):
- `POST /api/rooms/{roomId}/capture` — fotoğraf çeker
- `POST /api/rooms/{roomId}/capture/video/start` / `.../stop` — video kaydı
- `GET /api/rooms/{roomId}/capture/video/status` — kayıt durumu
- `GET /api/rooms/{roomId}/video-feed` — canlı MJPEG önizleme

Oda'dan bağımsız:
- `GET /api/captures?roomId=...` — kayıt listesi (roomId verilmezse tüm odalar; roomId verilirse o oda İLE ONUN `-cam1`, `-cam2` gibi alt-kameralarının kayıtları birlikte döner)
- `DELETE /api/captures/{id}` — soft-delete
- `POST /api/captures/{id}/refresh-metadata` — dosyadan metadata'yı tazele

WebSocket (API değil, ham bağlantı):
- `ws(s)://.../ws-camera/{roomId}` — tarayıcının kamera karelerini bu odaya akıttığı yer

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
- Endoskopi cihazının gerçekten UVC-uyumlu (tarayıcının doğrudan erişebileceği
  bir "kamera" gibi görünüp görünmediği) olup olmadığı henüz gerçek cihazla
  doğrulanmadı — şimdiye kadarki tüm testler bilgisayar/telefon kamerasıyla yapıldı.
