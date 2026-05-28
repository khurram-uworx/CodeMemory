namespace CodeMemory.Mcp;

public class CodeMemoryMcpOptions
{
    public string? RepoRoot { get; set; }
    public bool DebugMode { get; set; } = false;
    public string LogDirectory { get; set; } = ".codememory";
    public string Version { get; set; } = "unknown";
}
