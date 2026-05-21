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
    Text,
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
        [".txt"] = Language.Text,
        [".md"] = Language.Text,
    };

    public static Language Detect(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!extensionMap.TryGetValue(ext, out var language))
            return Language.Unknown;

        if (language == Language.C && ext.Equals(".h", StringComparison.OrdinalIgnoreCase))
            return sniffFile(filePath);

        return language;
    }

    public static Language Detect(string filePath, string fileContent)
    {
        var ext = Path.GetExtension(filePath);
        if (!extensionMap.TryGetValue(ext, out var language))
            return Language.Unknown;

        if (language == Language.C && ext.Equals(".h", StringComparison.OrdinalIgnoreCase))
            return sniffContent(fileContent.AsSpan());

        return language;
    }

    static Language sniffFile(string filePath)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            var buffer = new char[4096];
            var charsRead = reader.ReadBlock(buffer, 0, buffer.Length);
            return sniffContent(new string(buffer, 0, charsRead));
        }
        catch
        {
            return Language.C;
        }
    }

    static Language sniffContent(ReadOnlySpan<char> content)
    {
        // C++-only keywords that are vanishingly rare in C headers
        if (content.Contains("class", StringComparison.Ordinal) ||
            content.Contains("namespace", StringComparison.Ordinal) ||
            content.Contains("template", StringComparison.Ordinal) ||
            content.Contains("::", StringComparison.Ordinal))
            return Language.Cpp;

        return Language.C;
    }

    public static IReadOnlyCollection<string> SupportedExtensions
        => extensionMap.Keys;
}
