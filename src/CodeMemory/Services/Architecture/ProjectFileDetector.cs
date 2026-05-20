using Microsoft.Extensions.Logging;

namespace CodeMemory.Services.Architecture;

public sealed class ProjectFileDetector
{
    static readonly string[] KnownBuildFiles =
    [
        "*.csproj", "*.vbproj", "*.fsproj",
        "pom.xml",
        "package.json",
        "Cargo.toml",
        "go.mod",
        "build.gradle", "build.gradle.kts",
        "pyproject.toml",
        "CMakeLists.txt",
        "*.cabal",
    ];

    readonly ILogger<ProjectFileDetector> logger;

    public ProjectFileDetector(ILogger<ProjectFileDetector> logger)
        => this.logger = logger;

    public IReadOnlyDictionary<string, string> Discover(string repoRoot)
    {
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(repoRoot))
        {
            logger.LogWarning("Repo root does not exist: {RepoRoot}", repoRoot);
            return mapping;
        }

        foreach (var buildFile in KnownBuildFiles)
        {
            foreach (var filePath in Directory.EnumerateFiles(repoRoot, buildFile, SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (dir == null || !visited.Add(dir))
                    continue;

                var relativeDir = Path.GetRelativePath(repoRoot, dir).Replace('\\', '/');
                var componentName = Path.GetFileName(dir);

                mapping[relativeDir] = componentName;
                logger.LogDebug("Discovered component '{Component}' at '{Dir}' from {File}",
                    componentName, relativeDir, Path.GetFileName(filePath));
            }
        }

        logger.LogInformation("ProjectFileDetector: found {Count} components in {RepoRoot}",
            mapping.Count, repoRoot);

        return mapping;
    }
}
