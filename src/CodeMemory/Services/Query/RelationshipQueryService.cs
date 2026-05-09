using CodeMemory.Storage;

namespace CodeMemory.Services.Query;

public sealed class RelationshipQueryService
{
    readonly IStorageService storage;

    public RelationshipQueryService(IStorageService storage)
    {
        this.storage = storage;
    }

    public Task<RelationshipRecord?> GetByIdAsync(string id, CancellationToken ct = default)
        => storage.GetRelationshipAsync(id, ct);

    public Task<IReadOnlyList<RelationshipRecord>> GetBySourceAsync(string sourceSymbolId, CancellationToken ct = default)
        => storage.GetRelationshipsBySourceAsync(sourceSymbolId, ct);

    public Task<IReadOnlyList<RelationshipRecord>> GetByTargetAsync(string targetSymbolId, CancellationToken ct = default)
        => storage.GetRelationshipsByTargetAsync(targetSymbolId, ct);

    public async Task<IReadOnlyList<RelationshipRecord>> GetAllForSymbolAsync(
        string symbolId, CancellationToken ct = default)
    {
        var bySource = await storage.GetRelationshipsBySourceAsync(symbolId, ct);
        var byTarget = await storage.GetRelationshipsByTargetAsync(symbolId, ct);

        var combined = new List<RelationshipRecord>(bySource.Count + byTarget.Count);
        combined.AddRange(bySource);
        combined.AddRange(byTarget);
        return combined;
    }
}
