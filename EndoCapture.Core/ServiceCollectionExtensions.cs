using Endoscopy.Data;
using Endoscopy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Endoscopy;

/// <summary>
/// EndoCapture.Core servislerini ASP.NET Core DI konteynerine kaydeden
/// extension metodları.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Endoskopi kayıt sisteminin servis katmanını DI konteynerine kaydeder.
    ///
    /// Kaydedilen servisler:
    /// <list type="bullet">
    ///   <item><see cref="AppDbContext"/> — EF Core DbContext (Scoped)</item>
    ///   <item><see cref="CaptureDbService"/> — Medya kayıt CRUD servisi (Scoped)</item>
    ///   <item><see cref="CameraSessionManager"/> — Çoklu oda/kamera yöneticisi (Singleton)</item>
    ///   <item><see cref="CodecDetector"/> — Video codec tespiti ve önbellekleme (Singleton)</item>
    ///   <item><see cref="DeviceIdentityService"/> — Makine adı/IP/MAC tespiti (Singleton)</item>
    /// </list>
    ///
    /// Kullanım:
    /// <code>
    /// builder.Services.AddEndoCapture(options => {
    ///     options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;
    ///     options.StoragePath      = "storage";
    /// });
    /// </code>
    /// </summary>
    public static IServiceCollection AddEndoCapture(
        this IServiceCollection services,
        Action<EndoCaptureOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new EndoCaptureOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException(
                "EndoCapture: ConnectionString boş olamaz. " +
                "options.ConnectionString değerini ayarlayın.");

        // DbContext — her HTTP isteği kendi izole context'ini alır (thread-safe değil).
        services.AddDbContext<AppDbContext>(opt =>
            opt.UseNpgsql(options.ConnectionString));

        // Scoped servisler — DbContext'e bağımlı.
        services.AddScoped<CaptureDbService>();

        // Singleton servisler — uygulama ömrü boyunca tek örnek.
        services.AddSingleton<CameraSessionManager>();
        services.AddSingleton<CodecDetector>();
        services.AddSingleton<DeviceIdentityService>();

        // StoragePath'i Options pattern yerine basitçe saklıyoruz —
        // Controller bu değeri IWebHostEnvironment.ContentRootPath üzerinden
        // zaten yönetiyor; ileride gerekirse IOptions<EndoCaptureOptions> ile
        // genişletilebilir.
        services.Configure<EndoCaptureOptions>(o => o.StoragePath = options.StoragePath);

        return services;
    }
}
