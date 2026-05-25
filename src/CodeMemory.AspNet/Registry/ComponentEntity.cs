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
    public string ComponentKind { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string ComponentType { get; set; } = "Component";

    public RegisteredRepo RegisteredRepo { get; init; } = null!;
}
