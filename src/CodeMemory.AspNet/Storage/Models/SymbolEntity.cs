namespace CodeMemory.AspNet.Storage.Models;

public sealed class SymbolEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public int LineStart { get; set; }

    public int LineEnd { get; set; }

    public string FullName { get; set; } = string.Empty;

    public string? Modifiers { get; set; }

    public string? Documentation { get; set; }
}
