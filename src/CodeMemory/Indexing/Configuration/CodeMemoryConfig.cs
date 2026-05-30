using CodeMemory.Indexing.Parsing;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodeMemory.Indexing.Configuration;

public sealed record CodeMemoryConfig
{
    public List<string> Exclude { get; init; } = [];

    public Dictionary<string, string> LanguageOverrides { get; init; } = [];

    public double? ClusteringThreshold { get; init; }

    public static CodeMemoryConfig Default { get; } = new();

    public static CodeMemoryConfig Load(string repoRoot, ILogger? logger = null)
    {
        var path = Path.Combine(repoRoot, ".codememory.json");
        if (!File.Exists(path))
            return Default;

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<CodeMemoryConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (config is null)
            {
                logger?.LogWarning(".codememory.json at {Path} was empty or invalid — using defaults", path);
                return Default;
            }

            logger?.LogInformation("Loaded .codememory.json from {Path}: {ExcludeCount} exclusion patterns, {OverrideCount} language overrides",
                path, config.Exclude.Count, config.LanguageOverrides.Count);

            return config;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load .codememory.json from {Path} — using defaults", path);
            return Default;
        }
    }

    public Dictionary<string, Language> ResolvedLanguageOverrides(ILogger? logger = null)
    {
        if (LanguageOverrides.Count == 0)
            return [];

        var resolved = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in LanguageOverrides)
        {
            if (Enum.TryParse<Language>(value, ignoreCase: true, out var lang))
            {
                var ext = key.StartsWith('.') ? key : "." + key;
                resolved[ext] = lang;
                logger?.LogDebug("Language override: {Ext} → {Language}", ext, lang);
            }
            else
            {
                logger?.LogWarning("Invalid language '{Value}' in .codememory.json for key '{Key}' — skipping", value, key);
            }
        }
        return resolved;
    }
}
