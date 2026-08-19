using Endoscopy.Models;
using Microsoft.EntityFrameworkCore;

namespace Endoscopy.Data;

/// <summary>
/// PostgreSQL'e bağlanan EF Core context'i. Eskiden burada elle yazılmış SQL
/// string'leri ve manuel "ALTER TABLE IF NOT EXISTS" göçürmeleri vardı
/// (SQLite prototipinde); artık şema, gerçek model sınıflarından (bkz.
/// Models/) ve EF Core migration'larından üretiliyor.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<MediaCapture> MediaCaptures => Set<MediaCapture>();
    public DbSet<AiFinding> AiFindings => Set<AiFinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaCapture>(entity =>
        {
            entity.ToTable("MediaCaptures");
            entity.HasKey(e => e.Id);

            // Enum'ları okunabilir metin olarak sakla (Postgres'te "Photo", "Video" vb.
            // görünsün — integer koduna göre veritabanını elle incelemek çok daha zor olurdu).
            entity.Property(e => e.CaptureType)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            entity.Property(e => e.FilePath).IsRequired();
            entity.Property(e => e.TriggerSource).IsRequired();

            // GetActiveRecording sorgusu (WHERE Status = 'Recording') sık çalışıyor,
            // her kare için değil ama kayıt başlangıcı/bitişinde — index ucuz bir kazanç.
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CapturedAt);

            // "Bu hastanın/doktorun kayıtlarını göster" gibi aramalar için —
            // ortak diske geçişle birlikte bu tür filtrelemenin asıl kullanım
            // şekli olması bekleniyor (bkz. konuşma: "özelliklere tek tek bakmak
            // pratik değil, DB üzerinden aranmalı").
            entity.HasIndex(e => e.PatientIdentifier);
            entity.HasIndex(e => e.DoctorName);

            entity.HasMany(e => e.Findings)
                .WithOne(f => f.Capture!)
                .HasForeignKey(f => f.CaptureId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiFinding>(entity =>
        {
            entity.ToTable("AiFindings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FindingType).IsRequired();
            entity.Property(e => e.Description).IsRequired();
        });
    }
}
