using CodeMemory.AspNet.Storage.Models;
using CodeMemory.Storage;

namespace CodeMemory.AspNet.Storage;

public static class ModelMapping
{
    public static SymbolEntity ToEntity(this SymbolRecord record)
        => new()
        {
            Id = record.Id,
            Name = record.Name,
            Kind = record.Kind,
            FilePath = record.FilePath,
            LineStart = record.LineStart,
            LineEnd = record.LineEnd,
            FullName = record.FullName,
            Modifiers = record.Modifiers,
            Documentation = record.Documentation
        };

    public static SymbolRecord ToRecord(this SymbolEntity entity)
        => new()
        {
            Id = entity.Id,
            Name = entity.Name,
            Kind = entity.Kind,
            FilePath = entity.FilePath,
            LineStart = entity.LineStart,
            LineEnd = entity.LineEnd,
            FullName = entity.FullName,
            Modifiers = entity.Modifiers,
            Documentation = entity.Documentation
        };

    public static RelationshipEntity ToEntity(this RelationshipRecord record)
        => new()
        {
            Id = record.Id,
            SourceSymbolId = record.SourceSymbolId,
            TargetSymbolId = record.TargetSymbolId,
            RelationshipType = record.RelationshipType
        };

    public static RelationshipRecord ToRecord(this RelationshipEntity entity)
        => new()
        {
            Id = entity.Id,
            SourceSymbolId = entity.SourceSymbolId,
            TargetSymbolId = entity.TargetSymbolId,
            RelationshipType = entity.RelationshipType
        };
}
