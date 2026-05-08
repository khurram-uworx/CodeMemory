using CodeMemory.Indexing.Parsing;

namespace CodeMemory.Tests.Indexing.Parsing;

public sealed class LanguageDetectorTests
{
    [Test]
    public void Detect_WithCsExtension_ReturnsCSharp()
    {
        var result = LanguageDetector.Detect("foo.cs");
        Assert.That(result, Is.EqualTo(Language.CSharp));
    }

    [Test]
    public void Detect_WithPathContainingCs_ReturnsCSharp()
    {
        var result = LanguageDetector.Detect(@"src\CodeMemory\Program.cs");
        Assert.That(result, Is.EqualTo(Language.CSharp));
    }

    [TestCase("foo.js")]
    [TestCase("foo.py")]
    [TestCase("foo.ts")]
    [TestCase("foo.csproj")]
    [TestCase("foo")]
    public void Detect_WithNonCsExtension_ReturnsUnknown(string path)
    {
        var result = LanguageDetector.Detect(path);
        Assert.That(result, Is.EqualTo(Language.Unknown));
    }
}
