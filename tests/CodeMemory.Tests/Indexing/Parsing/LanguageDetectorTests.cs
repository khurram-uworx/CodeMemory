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

    [TestCase("foo.c", Language.C)]
    [TestCase("foo.h", Language.C)]
    [TestCase("foo.cpp", Language.Cpp)]
    [TestCase("foo.cc", Language.Cpp)]
    [TestCase("foo.cxx", Language.Cpp)]
    [TestCase("foo.hpp", Language.Cpp)]
    [TestCase("foo.hh", Language.Cpp)]
    [TestCase("foo.hxx", Language.Cpp)]
    public void Detect_WithCAndCppExtensions_ReturnsExpectedLanguage(string path, Language expected)
    {
        var result = LanguageDetector.Detect(path);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Detect_WithContent_H_WithCppKeywords_ReturnsCpp()
    {
        var content = """
            #ifndef FOO_H
            #define FOO_H
            class MyClass {
                int method();
            };
            #endif
            """;
        var result = LanguageDetector.Detect("foo.h", content);
        Assert.That(result, Is.EqualTo(Language.Cpp));
    }

    [Test]
    public void Detect_WithContent_H_WithOnlyCConstructs_ReturnsC()
    {
        var content = """
            #ifndef FOO_H
            #define FOO_H
            struct Point {
                int x;
                int y;
            };
            #endif
            """;
        var result = LanguageDetector.Detect("foo.h", content);
        Assert.That(result, Is.EqualTo(Language.C));
    }

    [Test]
    public void Detect_WithContent_H_WithNamespaceKeyword_ReturnsCpp()
    {
        var result = LanguageDetector.Detect("foo.h", "namespace myns { }");
        Assert.That(result, Is.EqualTo(Language.Cpp));
    }

    [Test]
    public void Detect_WithContent_H_WithTemplateKeyword_ReturnsCpp()
    {
        var result = LanguageDetector.Detect("foo.h", "template<typename T> class Foo {};");
        Assert.That(result, Is.EqualTo(Language.Cpp));
    }

    [Test]
    public void Detect_WithContent_H_WithScopeResolution_ReturnsCpp()
    {
        var result = LanguageDetector.Detect("foo.h", "void Foo::bar() {}");
        Assert.That(result, Is.EqualTo(Language.Cpp));
    }

    [Test]
    public void Detect_WithContent_CFile_ReturnsCRegardlessOfContent()
    {
        var result = LanguageDetector.Detect("foo.c", "class Foo {};");
        Assert.That(result, Is.EqualTo(Language.C));
    }

    [Test]
    public void Detect_WithContent_CppFile_ReturnsCppRegardlessOfContent()
    {
        var result = LanguageDetector.Detect("foo.cpp", "struct Foo { int x; };");
        Assert.That(result, Is.EqualTo(Language.Cpp));
    }
}
