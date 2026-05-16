namespace CodeMemory.Indexing.Parsing;

public enum Language
{
    Unknown,
    CSharp,
    TypeScript,
    JavaScript,
    Java,
    Python,
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
        [".html"] = Language.HTML,
        [".htm"] = Language.HTML,
    };

    public static Language Detect(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return extensionMap.TryGetValue(ext, out var language) ? language : Language.Unknown;
    }
}
