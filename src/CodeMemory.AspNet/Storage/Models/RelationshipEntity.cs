namespace CodeMemory.AspNet.Storage.Models;

public sealed class RelationshipEntity
{
    public string Id { get; set; } = string.Empty;

    public string SourceSymbolId { get; set; } = string.Empty;

    public string TargetSymbolId { get; set; } = string.Empty;

    public string RelationshipType { get; set; } = string.Empty;
}
