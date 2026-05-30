namespace CodeMemory.Mcp;

static class CliParser
{
    public static (string? RepoRoot, bool Debug, bool Help, bool Version, bool Init) Parse(string[] args)
    {
        var repoRoot = (string?)null;
        var debug = false;
        var help = false;
        var version = false;
        var init = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo" or "-r" when i + 1 < args.Length:
                    repoRoot = args[++i];
                    break;
                case "--debug":
                    debug = true;
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                case "--version" or "-v":
                    version = true;
                    break;
                case "--init" or "-i":
                    init = true;
                    break;
            }
        }

        return (repoRoot, debug, help, version, init);
    }
}
