using CodeMemory.Storage;
using Microsoft.Extensions.Logging;

namespace CodeMemory.Services.Architecture;

public sealed class ProjectFileDetector
{
    static ComponentType inferComponentType(string relativeDir)
    {
        var segments = relativeDir.Split(DirSeparators, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => string.Equals(s, "test", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "tests", StringComparison.OrdinalIgnoreCase))
            ? ComponentType.Test
            : ComponentType.Component;
    }

    static readonly (string Pattern, ComponentKind Kind)[] KnownBuildFiles =
    [
        ("*.csproj", ComponentKind.MsBuild),
        ("*.vbproj", ComponentKind.MsBuild),
        ("*.fsproj", ComponentKind.MsBuild),
        ("pom.xml", ComponentKind.Maven),
        ("package.json", ComponentKind.Node),
        ("Cargo.toml", ComponentKind.Cargo),
        ("go.mod", ComponentKind.Go),
        ("build.gradle", ComponentKind.Gradle),
        ("build.gradle.kts", ComponentKind.Gradle),
        ("pyproject.toml", ComponentKind.Python),
        ("CMakeLists.txt", ComponentKind.CMake),
        ("*.cabal", ComponentKind.Haskell),
    ];

    static readonly char[] DirSeparators = ['/', '\\'];

    readonly ILogger<ProjectFileDetector> logger;

    public ProjectFileDetector(ILogger<ProjectFileDetector> logger)
        => this.logger = logger;

    public static ComponentKind? IsKnownBuildFile(string fileName)
    {
        foreach (var (pattern, kind) in KnownBuildFiles)
        {
            if (pattern.StartsWith("*."))
                if (string.Equals(Path.GetExtension(fileName), pattern[1..], StringComparison.OrdinalIgnoreCase))
                    return kind;
            else
                if (string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase))
                    return kind;
        }

        return null;
    }

    public IReadOnlyList<ComponentInformation> Discover(string repoRoot)
        => Discover(repoRoot, null);

    public IReadOnlyList<ComponentInformation> Discover(string repoRoot, IReadOnlyList<string>? preCollectedFiles)
    {
        var components = new List<ComponentInformation>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(repoRoot))
        {
            logger.LogWarning("Repo root does not exist: {RepoRoot}", repoRoot);
            return components;
        }

        if (preCollectedFiles != null)
        {
            foreach (var filePath in preCollectedFiles)
            {
                var dir = Path.GetDirectoryName(filePath);
                if (dir == null || !visited.Add(dir))
                    continue;

                var relativeDir = Path.GetRelativePath(repoRoot, dir).Replace('\\', '/');
                var componentName = Path.GetFileName(dir);
                var kind = IsKnownBuildFile(Path.GetFileName(filePath)) ?? ComponentKind.MsBuild;
                var componentType = inferComponentType(relativeDir);
                var fileCount = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();

                components.Add(new ComponentInformation(relativeDir, componentName, kind, componentType, fileCount));
                logger.LogDebug("Discovered component '{Component}' ({Kind}, {Type}) at '{Dir}' from {File}",
                    componentName, kind, componentType, relativeDir, Path.GetFileName(filePath));
            }
        }
        else
        {
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
                    var fileCount = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();

                    components.Add(new ComponentInformation(relativeDir, componentName, kind, componentType, fileCount));
                    logger.LogDebug("Discovered component '{Component}' ({Kind}, {Type}) at '{Dir}' from {File}",
                        componentName, kind, componentType, relativeDir, Path.GetFileName(filePath));
                }
            }
        }

        logger.LogInformation("ProjectFileDetector: found {Count} components in {RepoRoot}",
            components.Count, repoRoot);

        return components;
    }
}
