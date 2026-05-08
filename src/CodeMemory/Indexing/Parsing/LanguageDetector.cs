namespace CodeMemory.Indexing.Parsing;

public enum Language
{
    Unknown,
    CSharp,
}

public static class LanguageDetector
{
    static readonly Dictionary<string, Language> extensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = Language.CSharp,
    };

    public static Language Detect(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return extensionMap.TryGetValue(ext, out var language) ? language : Language.Unknown;
    }
}
