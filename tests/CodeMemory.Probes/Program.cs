namespace CodeMemory.Probes;

/// <summary>
/// Probe: the CodeMemory probe console — a run-manually accumulation point for
/// standalone diagnostics (grammar shapes, parser behavior, indexing output).
/// Not part of the NUnit suite; `dotnet test` and CI never touch this project.
/// Add a probe as an <c>internal static class XxxProbe { public static int Run() }</c>
/// and register it in the switch below.
/// </summary>
internal class Program
{
    static int Main(string[] args)
    {
        Console.WriteLine("=== CodeMemory Probes ===");
        Console.WriteLine($"Runtime: {Environment.Version}");
        Console.WriteLine();

        string mode = args.Length > 0 ? args[0] : "all";
        return mode switch
        {
            "tree-sitter" => TreeSitterGrammarProbe.Run(),
            "all" => TreeSitterGrammarProbe.Run(),
            _ => Usage()
        };
    }

    static int Usage()
    {
        Console.WriteLine("Usage: dotnet run --project tests/CodeMemory.Probes -- [probe]");
        Console.WriteLine();
        Console.WriteLine("Available probes:");
        Console.WriteLine("  tree-sitter    Dump Java/TypeScript/C++ parse trees and name-qualification chains");
        return 1;
    }
}