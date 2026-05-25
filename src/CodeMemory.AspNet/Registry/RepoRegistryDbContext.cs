using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CodeMemory.AspNet.Registry;

[Table("RegisteredRepos")]
public sealed class RegisteredRepo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; init; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? GitUrl { get; set; }

    [Required, MaxLength(1000)]
    public string LocalPath { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Branch { get; set; }

    [Required, MaxLength(50)]
    public string CloneStatus { get; set; } = "Pending";

    [Required, MaxLength(50)]
    public string IndexStatus { get; set; } = "Pending";

    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime? LastIndexedAt { get; set; }
}

public sealed class RepoRegistryDbContext : DbContext
{
    public RepoRegistryDbContext(DbContextOptions<RepoRegistryDbContext> options) : base(options)
    { }

    public DbSet<RegisteredRepo> RegisteredRepos
        => Set<RegisteredRepo>();

    public DbSet<ComponentEntity> Components
        => Set<ComponentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RegisteredRepo>(entity =>
        {
            entity.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<ComponentEntity>(entity =>
        {
            entity.ToTable("Components");
            entity.HasKey(c => new { c.RegisteredRepoId, c.BuildFilePath });

            entity.Property(c => c.BuildFilePath).HasColumnName("build_file_path").IsRequired().HasMaxLength(1000);
            entity.Property(c => c.ComponentName).HasColumnName("component_name").IsRequired().HasMaxLength(500);
            entity.Property(c => c.ComponentKindString).HasColumnName("component_kind").IsRequired().HasMaxLength(100);
            entity.Property(c => c.ComponentTypeString).HasColumnName("component_type").IsRequired().HasMaxLength(100);
            entity.Property(c => c.FileCount).HasColumnName("file_count").HasDefaultValue(0);
            entity.Property(c => c.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
            entity.Property(c => c.DeletedAt).HasColumnName("deleted_at");

            entity.HasOne(c => c.RegisteredRepo)
                  .WithMany()
                  .HasForeignKey(c => c.RegisteredRepoId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(c => c.RegisteredRepoId).HasDatabaseName("IX_Components_RegisteredRepoId");
        });
    }
}
