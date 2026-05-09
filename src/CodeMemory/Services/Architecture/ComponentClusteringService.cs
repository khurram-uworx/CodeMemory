using CodeMemory.Indexing.Architecture;
using CodeMemory.Storage.Services;

namespace CodeMemory.Services.Architecture;

public sealed class ComponentClusteringService : IComponentClusteringService
{
    static IReadOnlyList<ComponentCluster> clusterComponents(
        string[] components,
        Dictionary<string, Dictionary<string, int>> matrix,
        double threshold)
    {
        var n = components.Length;
        var adj = new bool[n, n];

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var fromItoJ = matrix[components[i]].GetValueOrDefault(components[j], 0);
                var fromJtoI = matrix[components[j]].GetValueOrDefault(components[i], 0);
                var totalInter = fromItoJ + fromJtoI;

                var totalFromI = matrix[components[i]].Values.Sum();
                var totalFromJ = matrix[components[j]].Values.Sum();
                var totalEdges = totalFromI + totalFromJ;

                var coupling = totalEdges > 0 ? (double)totalInter / totalEdges : 0;
                adj[i, j] = adj[j, i] = coupling >= threshold;
            }
        }

        var visited = new bool[n];
        var result = new List<ComponentCluster>();

        for (var i = 0; i < n; i++)
        {
            if (visited[i])
                continue;

            var members = new List<string>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            visited[i] = true;

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                members.Add(components[cur]);

                for (var k = 0; k < n; k++)
                {
                    if (!visited[k] && adj[cur, k])
                    {
                        visited[k] = true;
                        queue.Enqueue(k);
                    }
                }
            }

            var cohesion = members.Count > 1 ? computeCohesion(members, matrix) : 1.0;
            var name = string.Join("+", members.OrderBy(m => m));
            result.Add(new ComponentCluster(name, members.OrderBy(m => m).ToList(), cohesion));
        }

        return result.OrderByDescending(c => c.CohesionScore).ToList();
    }

    static double computeCohesion(
        IReadOnlyList<string> members,
        Dictionary<string, Dictionary<string, int>> matrix)
    {
        var internalEdges = 0;
        var totalEdges = 0;

        foreach (var a in members)
        {
            foreach (var (target, count) in matrix[a])
            {
                totalEdges += count;
                if (members.Contains(target))
                    internalEdges += count;
            }
        }

        return totalEdges > 0 ? (double)internalEdges / totalEdges : 1.0;
    }

    static string getTopLevelDirectory(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        var trimmed = normalized.TrimStart('/');
        var slashIndex = trimmed.IndexOf('/');
        return slashIndex > 0 ? trimmed[..slashIndex] : trimmed;
    }

    static readonly string[] knownKinds = ["Class", "Interface", "Struct", "Enum", "Record", "Method", "Property", "Field", "Event"];

    readonly IStorageService storage;
    readonly ILogger<ComponentClusteringService> logger;

    public ComponentClusteringService(IStorageService storage, ILogger<ComponentClusteringService> logger)
    {
        this.storage = storage;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<ComponentCluster>> GetClustersAsync(
        double threshold = 0.3, CancellationToken ct = default)
    {
        threshold = Math.Clamp(threshold, 0.01, 1.0);
        var symbolsPerKind = new List<Storage.Models.SymbolRecord>();
        foreach (var kind in knownKinds)
        {
            var batch = await storage.GetSymbolsByKindAsync(kind, 100000, ct);
            symbolsPerKind.AddRange(batch);
        }

        if (symbolsPerKind.Count == 0)
            return [];

        var symbolToComponent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var componentFiles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var sym in symbolsPerKind)
        {
            var component = getTopLevelDirectory(sym.FilePath);
            symbolToComponent[sym.FullName] = component;
            if (!componentFiles.ContainsKey(component))
                componentFiles[component] = [];
            componentFiles[component].Add(sym.FilePath);
        }

        var componentNames = componentFiles.Keys.OrderBy(c => c).ToArray();
        var matrix = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var comp in componentNames)
            matrix[comp] = [];

        foreach (var sym in symbolsPerKind)
        {
            var rels = await storage.GetRelationshipsBySourceAsync(sym.FullName, ct);
            var sourceComponent = symbolToComponent.GetValueOrDefault(sym.FullName);
            if (sourceComponent == null)
                continue;

            foreach (var rel in rels)
            {
                var targetName = rel.TargetSymbolId;
                var targetComponent = symbolToComponent.GetValueOrDefault(targetName);
                if (targetComponent == null)
                    continue;

                if (!matrix[sourceComponent].ContainsKey(targetComponent))
                    matrix[sourceComponent][targetComponent] = 0;
                matrix[sourceComponent][targetComponent]++;
            }
        }

        var clusters = clusterComponents(componentNames, matrix, threshold);
        logger.LogDebug("GetClustersAsync(threshold={Threshold}): {Count} clusters from {Components} components",
            threshold, clusters.Count, componentNames.Length);
        return clusters;
    }
}
