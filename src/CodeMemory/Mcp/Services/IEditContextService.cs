using CodeMemory.Mcp.Models;

namespace CodeMemory.Mcp.Services;

public interface IEditContextService
{
    Task<EditContext> GetEditContextAsync(string symbolPath, EditContextOptions options, CancellationToken ct = default);
}
