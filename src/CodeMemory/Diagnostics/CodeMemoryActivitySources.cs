using System.Diagnostics;

namespace CodeMemory.Diagnostics;

public static class CodeMemoryActivitySources
{
    public static readonly ActivitySource Indexing = new("CodeMemory.Indexing", "1.0");
    public static readonly ActivitySource Git = new("CodeMemory.Git", "1.0");
    public static readonly ActivitySource Search = new("CodeMemory.Search", "1.0");
    public static readonly ActivitySource Sql = new("CodeMemory.Sql", "1.0");
    public static readonly ActivitySource Architecture = new("CodeMemory.Architecture", "1.0");
}
