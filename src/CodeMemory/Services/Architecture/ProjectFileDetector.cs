using CodeMemory.Storage;
using Microsoft.Extensions.Logging;

namespace CodeMemory.Services.Architecture;

public sealed class ProjectFileDetector
{
    static readonly (string Pattern, string Kind)[] KnownBuildFiles =
    [
        ("*.csproj", "MsBuild"),
        ("*.vbproj", "MsBuild"),
        ("*.fsproj", "MsBuild"),
        ("pom.xml", "Maven"),
        ("package.json", "Node"),
        ("Cargo.toml", "Cargo"),
        ("go.mod", "Go"),
        ("build.gradle", "Gradle"),
        ("build.gradle.kts", "Gradle"),
        ("pyproject.toml", "Python"),
        ("CMakeLists.txt", "CMake"),
        ("*.cabal", "Haskell"),
    ];

    static readonly char[] DirSeparators = ['/', '\\'];

    static string inferComponentType(string relativeDir)
    {
        var segments = relativeDir.Split(DirSeparators, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => string.Equals(s, "test", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "tests", StringComparison.OrdinalIgnoreCase))
            ? "Test"
            : "Component";
    }

    readonly ILogger<ProjectFileDetector> logger;

    public ProjectFileDetector(ILogger<ProjectFileDetector> logger)
        => this.logger = logger;

    public IReadOnlyList<ComponentMappingInfo> Discover(string repoRoot)
    {
        var components = new List<ComponentMappingInfo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(repoRoot))
        {
            logger.LogWarning("Repo root does not exist: {RepoRoot}", repoRoot);
            return components;
        }

        foreach (var (pattern, kind) in KnownBuildFiles)
        {
            foreach (var filePath in Directory.EnumerateFiles(repoRoot, pattern, SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (dir == null || !visited.Add(dir))
                    continue;

                var relativeDir = Path.GetRelativePath(repoRoot, dir).Replace('\\', '/');
                var componentName = Path.GetFileName(dir);
                var componentType = inferComponentType(relativeDir);

                components.Add(new ComponentMappingInfo(relativeDir, componentName, kind, componentType));
                logger.LogDebug("Discovered component '{Component}' ({Kind}, {Type}) at '{Dir}' from {File}",
                    componentName, kind, componentType, relativeDir, Path.GetFileName(filePath));
            }
        }

        logger.LogInformation("ProjectFileDetector: found {Count} components in {RepoRoot}",
            components.Count, repoRoot);

        return components;
    }
}
