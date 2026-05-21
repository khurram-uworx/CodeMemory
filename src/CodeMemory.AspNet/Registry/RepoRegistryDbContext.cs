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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RegisteredRepo>(entity =>
        {
            entity.HasIndex(r => r.Name).IsUnique();
        });
    }
}
