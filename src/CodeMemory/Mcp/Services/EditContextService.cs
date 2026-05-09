using CodeMemory.Indexing.Graph;
using CodeMemory.Mcp.Models;
using CodeMemory.Storage;

namespace CodeMemory.Mcp.Services;

public sealed class EditContextService : IEditContextService
{
    readonly IStorageService? storage;
    readonly IDependencyGraphService? graphService;

    public EditContextService(IServiceProvider serviceProvider)
    {
        storage = serviceProvider.GetService<IStorageService>();
        graphService = serviceProvider.GetService<IDependencyGraphService>();
    }

    public async Task<EditContext> GetEditContextAsync(
        string symbolPath, EditContextOptions options, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        // Resolve target symbol
        DependencyNode? target = null;
        if (storage != null)
        {
            try
            {
                var symbol = await storage.GetSymbolAsync(symbolPath, ct);
                if (symbol != null)
                {
                    target = new DependencyNode(
                        symbol.Name, symbol.FilePath, symbol.Kind,
                        $"{symbol.LineStart}-{symbol.LineEnd}", "self");
                }
                else
                {
                    warnings.Add($"Symbol '{symbolPath}' not found in index");
                    target = new DependencyNode(symbolPath, "", "", "", "unknown");
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Symbol lookup failed: {ex.Message}");
                target = new DependencyNode(symbolPath, "", "", "", "unknown");
            }
        }
        else
        {
            warnings.Add("Storage service not available");
            target = new DependencyNode(symbolPath, "", "", "", "unknown");
        }

        // Resolve source code
        string? sourceCode = null;
        if (options.IncludeSourceCode && storage != null && target.FilePath != "")
        {
            try
            {
                var chunks = await storage.GetChunksBySymbolAsync(symbolPath, ct);
                if (chunks.Count > 0)
                    sourceCode = string.Join("\n", chunks.Select(c => c.Content));
            }
            catch (Exception ex)
            {
                warnings.Add($"Source code retrieval failed: {ex.Message}");
            }
        }

        // Resolve dependencies
        IReadOnlyList<DependencyNode>? deps = null;
        IReadOnlyList<DependencyNode>? related = null;
        IReadOnlyList<string>? tests = null;

        if (options.IncludeDependencies && graphService != null)
        {
            try
            {
                deps = await graphService.TraceAsync(symbolPath, "both", options.Depth, ct);
            }
            catch (Exception ex)
            {
                warnings.Add($"Dependency tracing failed: {ex.Message}");
            }

            try
            {
                related = await graphService.FindRelatedAsync(symbolPath, "all", ct);
            }
            catch (Exception ex)
            {
                warnings.Add($"Related symbol lookup failed: {ex.Message}");
            }

            try
            {
                var testFiles = await graphService.FindTestCoverageAsync(symbolPath, ct);
                tests = testFiles.Count > 0 ? testFiles : null;
            }
            catch (Exception ex)
            {
                warnings.Add($"Test coverage lookup failed: {ex.Message}");
            }
        }

        var targetInfo = target != null
            ? new TargetInfo(target.SymbolName, target.FilePath, target.LineRange, target.Kind)
            : new TargetInfo(symbolPath, "", "", "");

        return new EditContext(
            Target: targetInfo,
            SourceCode: sourceCode,
            Dependencies: deps,
            RelatedSymbols: related,
            Tests: tests,
            Timestamp: DateTimeOffset.UtcNow,
            Warnings: warnings.Count > 0 ? warnings : null);
    }
}
