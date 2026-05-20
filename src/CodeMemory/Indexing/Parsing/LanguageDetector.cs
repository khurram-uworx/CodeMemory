namespace CodeMemory.Indexing.Parsing;

public enum Language
{
    Unknown,
    CSharp,
    TypeScript,
    JavaScript,
    Java,
    Python,
    Go,
    Rust,
    C,
    Cpp,
    HTML,
}

public static class LanguageDetector
{
    static readonly Dictionary<string, Language> extensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = Language.CSharp,
        [".ts"] = Language.TypeScript,
        [".tsx"] = Language.TypeScript,
        [".js"] = Language.JavaScript,
        [".jsx"] = Language.JavaScript,
        [".java"] = Language.Java,
        [".py"] = Language.Python,
        [".go"] = Language.Go,
        [".rs"] = Language.Rust,
        [".c"] = Language.C,
        [".h"] = Language.C,
        [".cpp"] = Language.Cpp,
        [".cc"] = Language.Cpp,
        [".cxx"] = Language.Cpp,
        [".hpp"] = Language.Cpp,
        [".hh"] = Language.Cpp,
        [".hxx"] = Language.Cpp,
        [".html"] = Language.HTML,
        [".htm"] = Language.HTML,
    };

    public static Language Detect(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return extensionMap.TryGetValue(ext, out var language) ? language : Language.Unknown;
    }

    public static IReadOnlyCollection<string> SupportedExtensions
        => extensionMap.Keys;
}
