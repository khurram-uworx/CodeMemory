namespace CodeMemory.Mcp;

/// <summary>Configuration options for the CodeMemory MCP host.</summary>
public class CodeMemoryMcpOptions
{
    /// <summary>Repository root path.</summary>
    public string? RepoRoot { get; set; }
    /// <summary>When true, runs indexing synchronously with verbose logging.</summary>
    public bool DebugMode { get; set; } = false;
    /// <summary>Directory for log output.</summary>
    public string LogDirectory { get; set; } = ".codememory";
    /// <summary>Application version string.</summary>
    public string Version { get; set; } = "unknown";
}
