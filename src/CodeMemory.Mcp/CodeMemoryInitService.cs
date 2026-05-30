using CodeMemory.Indexing;
using Microsoft.Extensions.Logging;

namespace CodeMemory.Mcp;

public sealed record InitResult(
    string Status,
    string RepoRoot,
    string? ConfigPath,
    bool ConfigCreated,
    bool GitIgnoreUpdated,
    string Message
);

public sealed class CodeMemoryInitService(ILogger<CodeMemoryInitService>? logger = null)
{
    static readonly string DefaultConfigJson = $$"""
        {
            "exclude": [],
            "languageOverrides": {},
            "clusteringThreshold": null
        }
        """;

    public InitResult Run(string repoRoot)
    {
        var configPath = Path.Combine(repoRoot, ".codememory.json");
        var configCreated = false;
        var gitIgnoreUpdated = false;

        if (File.Exists(configPath))
        {
            logger?.LogInformation(".codememory.json already exists at {Path}, skipping creation", configPath);
        }
        else
        {
            try
            {
                Directory.CreateDirectory(repoRoot);
                File.WriteAllText(configPath, DefaultConfigJson);
                configCreated = true;
                logger?.LogInformation("Created .codememory.json at {Path}", configPath);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to create .codememory.json at {Path}", configPath);
                return new InitResult("error", repoRoot, null, false, false,
                    $"Failed to create .codememory.json: {ex.Message}");
            }
        }

        var gitIgnorePath = Path.Combine(repoRoot, ".gitignore");
        if (File.Exists(gitIgnorePath))
        {
            var parser = GitIgnoreParser.Load(gitIgnorePath);
            if (parser.IsIgnored(".codememory.json"))
            {
                logger?.LogInformation(".codememory.json already covered by .gitignore, skipping update");
            }
            else
            {
                try
                {
                    File.AppendAllText(gitIgnorePath, $"{Environment.NewLine}# CodeMemory repository config{Environment.NewLine}.codememory.json{Environment.NewLine}");
                    gitIgnoreUpdated = true;
                    logger?.LogInformation("Appended .codememory.json to .gitignore at {Path}", gitIgnorePath);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Failed to update .gitignore at {Path}", gitIgnorePath);
                    return new InitResult("partial", repoRoot, configPath, configCreated, false,
                        $"Created .codememory.json but failed to update .gitignore: {ex.Message}");
                }
            }
        }
        else
        {
            try
            {
                File.WriteAllText(gitIgnorePath, $"# CodeMemory repository config{Environment.NewLine}.codememory.json{Environment.NewLine}");
                gitIgnoreUpdated = true;
                logger?.LogInformation("Created .gitignore at {Path} with .codememory.json entry", gitIgnorePath);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to create .gitignore at {Path}", gitIgnorePath);
                return new InitResult("partial", repoRoot, configPath, configCreated, false,
                    $"Created .codememory.json but failed to create .gitignore: {ex.Message}");
            }
        }

        return new InitResult("ok", repoRoot, configPath, configCreated, gitIgnoreUpdated,
            configCreated
                ? gitIgnoreUpdated
                    ? "Created .codememory.json with default settings and added to .gitignore"
                    : "Created .codememory.json with default settings (already in .gitignore)"
                : gitIgnoreUpdated
                    ? ".codememory.json already exists; added .codememory.json to .gitignore"
                    : ".codememory.json already exists and is already in .gitignore — nothing to do");
    }
}
