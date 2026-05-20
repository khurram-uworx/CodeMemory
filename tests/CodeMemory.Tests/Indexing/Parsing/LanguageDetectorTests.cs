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

    [TestCase("foo.rb")]
    [TestCase("foo.csproj")]
    [TestCase("foo")]
    public void Detect_WithNonCsExtension_ReturnsUnknown(string path)
    {
        var result = LanguageDetector.Detect(path);
        Assert.That(result, Is.EqualTo(Language.Unknown));
    }

    [TestCase("foo.ts")]
    [TestCase("foo.tsx")]
    [TestCase("foo.js")]
    [TestCase("foo.jsx")]
    public void Detect_WithTypeScriptJavaScriptExtension_ReturnsCorrectLanguage(string path)
    {
        var result = LanguageDetector.Detect(path);
        Assert.That(result, Is.Not.EqualTo(Language.Unknown));
    }

    [Test]
    public void Detect_WithJavaExtension_ReturnsJava()
    {
        var result = LanguageDetector.Detect("Foo.java");
        Assert.That(result, Is.EqualTo(Language.Java));
    }

    [Test]
    public void Detect_WithPythonExtension_ReturnsPython()
    {
        var result = LanguageDetector.Detect("foo.py");
        Assert.That(result, Is.EqualTo(Language.Python));
    }

    [Test]
    public void Detect_WithGoExtension_ReturnsGo()
    {
        var result = LanguageDetector.Detect("foo.go");
        Assert.That(result, Is.EqualTo(Language.Go));
    }

    [Test]
    public void Detect_WithRustExtension_ReturnsRust()
    {
        var result = LanguageDetector.Detect("foo.rs");
        Assert.That(result, Is.EqualTo(Language.Rust));
    }

    [TestCase("foo.html")]
    [TestCase("foo.htm")]
    public void Detect_WithHtmlExtension_ReturnsHtml(string path)
    {
        var result = LanguageDetector.Detect(path);
        Assert.That(result, Is.EqualTo(Language.HTML));
    }
}
