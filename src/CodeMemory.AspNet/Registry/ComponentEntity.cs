using System.ComponentModel.DataAnnotations;

namespace CodeMemory.AspNet.Registry;

public sealed class ComponentEntity
{
    public int RegisteredRepoId { get; init; }

    [Required, MaxLength(1000)]
    public string BuildFilePath { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string ComponentName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string ComponentKindString { get; set; } = "Unknown";

    [Required, MaxLength(100)]
    public string ComponentTypeString { get; set; } = "Component";

    public int FileCount { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public RegisteredRepo RegisteredRepo { get; init; } = null!;
}
