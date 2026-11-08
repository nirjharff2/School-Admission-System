using AdmissionSystem.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AdmissionSystem.Api.Data;

/// <summary>Names of database constraints the application needs to recognise in errors.</summary>
public static class DbConstraints
{
    public const string ActiveHoldPerStudentAndClass = "ux_holds_active_student_class";
}

public class AdmissionDbContext(DbContextOptions<AdmissionDbContext> options) : DbContext(options)
{
    public DbSet<SchoolClass> Classes => Set<SchoolClass>();
    public DbSet<Hold> Holds => Set<Hold>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SchoolClass>(e =>
        {
            e.ToTable("classes", t => t.HasCheckConstraint("ck_classes_total_seats_positive", "total_seats >= 1"));
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").HasMaxLength(40);
            e.Property(c => c.Name).HasColumnName("name").HasMaxLength(Limits.ClassNameMaxLength).IsRequired();
            e.Property(c => c.TotalSeats).HasColumnName("total_seats");
            e.Property(c => c.CreatedAt).HasColumnName("created_at");
            e.HasIndex(c => c.CreatedAt);
        });

        modelBuilder.Entity<Hold>(e =>
        {
            e.ToTable("holds");
            e.HasKey(h => h.Id);
            e.Property(h => h.Id).HasColumnName("id").HasMaxLength(40);
            e.Property(h => h.ClassId).HasColumnName("class_id").HasMaxLength(40).IsRequired();
            e.Property(h => h.StudentId).HasColumnName("student_id").HasMaxLength(17).IsRequired();
            e.Property(h => h.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
            e.Property(h => h.CreatedAt).HasColumnName("created_at");
            e.Property(h => h.ExpiresAt).HasColumnName("expires_at");
            e.HasOne(h => h.Class).WithMany(c => c.Holds).HasForeignKey(h => h.ClassId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(h => new { h.ClassId, h.Status, h.ExpiresAt });
            e.HasIndex(h => new { h.StudentId, h.CreatedAt });

            // Backstop for SPEC 5.3: at most one active (HELD or CONFIRMED) hold per student and class. CreateHold stays
            // inside the class row lock, so this only fires if some other path ever skips that lock.
            e.HasIndex(h => new { h.ClassId, h.StudentId })
                .IsUnique()
                .HasDatabaseName(DbConstraints.ActiveHoldPerStudentAndClass)
                .HasFilter("status IN ('HELD', 'CONFIRMED')");

            // Serves the expiry sweeper (status = 'HELD' AND expires_at <= now), which the (class_id, ...) index cannot.
            e.HasIndex(h => h.ExpiresAt)
                .HasDatabaseName("ix_holds_held_expires_at")
                .HasFilter("status = 'HELD'");
        });
    }
}
