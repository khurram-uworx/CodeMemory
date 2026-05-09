using CodeMemory.Storage;

namespace CodeMemory.Services.Query;

public sealed class SymbolQueryService
{
    readonly IStorageService storage;

    public SymbolQueryService(IStorageService storage)
    {
        this.storage = storage;
    }

    public Task<SymbolRecord?> GetByIdAsync(string id, CancellationToken ct = default)
        => storage.GetSymbolAsync(id, ct);

    public Task<IReadOnlyList<SymbolRecord>> GetByFileAsync(string filePath, int top = 100, CancellationToken ct = default)
        => storage.GetSymbolsByFileAsync(filePath, top, ct);

    public Task<IReadOnlyList<SymbolRecord>> GetByKindAsync(string kind, int top = 100, CancellationToken ct = default)
        => storage.GetSymbolsByKindAsync(kind, top, ct);
}
