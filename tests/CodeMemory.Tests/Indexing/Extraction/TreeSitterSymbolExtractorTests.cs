using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing.Extraction;

public sealed class TreeSitterSymbolExtractorTests
{
    static readonly string tempDir = Path.GetTempPath();

    static bool IsTreeSitterAvailable()
    {
        try
        {
            using var parser = new TreeSitter.Parser(new TreeSitter.Language("TypeScript"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    static async Task<(IReadOnlyList<Symbol> Symbols, ParseResult Result)> ExtractFromCode(
        string code, string extension)
    {
        var parser = new TreeSitterParser(NullLogger<TreeSitterParser>.Instance);
        var extractor = new TreeSitterSymbolExtractor(NullLogger<TreeSitterSymbolExtractor>.Instance);
        var path = Path.Combine(tempDir, $"{Guid.NewGuid()}{extension}");
        try
        {
            await File.WriteAllTextAsync(path, code);
            var result = await parser.ParseAsync(path);
            Assert.That(result, Is.Not.Null);
            var symbols = extractor.Extract(result!, path);
            return (symbols, result!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Extract_TypeScript_ContainsClassAndInterface()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class MyClass { }
            interface MyInterface { }
            """;

        var (symbols, _) = await ExtractFromCode(code, ".ts");

        Assert.That(symbols.Any(s => s.Name == "MyClass" && s.Kind == CodeSymbolKind.Class), Is.True);
        Assert.That(symbols.Any(s => s.Name == "MyInterface" && s.Kind == CodeSymbolKind.Interface), Is.True);
    }

    [Test]
    public async Task Extract_TypeScript_ContainsFunctionAndVariable()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            function greet(name: string): void {}
            const version = 42;
            """;

        var (symbols, _) = await ExtractFromCode(code, ".ts");

        Assert.That(symbols.Any(s => s.Name.StartsWith("greet") && s.Kind == CodeSymbolKind.Function), Is.True);
        Assert.That(symbols.Any(s => s.Name == "version" && s.Kind == CodeSymbolKind.Variable), Is.True);
    }

    [Test]
    public async Task Extract_TypeScript_NestedTypesHaveFullName()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Outer {
                innerMethod() {}
            }
            """;

        var (symbols, _) = await ExtractFromCode(code, ".ts");

        var method = symbols.FirstOrDefault(s => s.Name.StartsWith("innerMethod"));
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.FullName, Is.EqualTo("Outer.innerMethod()"));
    }

    [Test]
    public async Task Extract_TypeScript_ExportModifierDetected()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            export class ExportedClass { }
            """;

        var (symbols, _) = await ExtractFromCode(code, ".ts");

        var cls = symbols.FirstOrDefault(s => s.Name == "ExportedClass");
        Assert.That(cls, Is.Not.Null);
        Assert.That(cls!.Modifiers, Does.Contain("export"));
    }

    [Test]
    public async Task Extract_JavaScript_ContainsClassAndFunction()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class MyClass { }
            function doStuff() {}
            """;

        var (symbols, _) = await ExtractFromCode(code, ".js");

        Assert.That(symbols.Any(s => s.Name == "MyClass" && s.Kind == CodeSymbolKind.Class), Is.True);
        Assert.That(symbols.Any(s => s.Name.StartsWith("doStuff") && s.Kind == CodeSymbolKind.Function), Is.True);
    }

    [Test]
    public async Task Extract_Java_ContainsClassAndMethod()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            public class MyJavaClass {
                public void doSomething(String arg) {}
            }
            """;

        var (symbols, _) = await ExtractFromCode(code, ".java");

        var cls = symbols.FirstOrDefault(s => s.Name == "MyJavaClass");
        Assert.That(cls, Is.Not.Null);
        Assert.That(cls!.Kind, Is.EqualTo(CodeSymbolKind.Class));

        var method = symbols.FirstOrDefault(s => s.Name.StartsWith("doSomething"));
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.Kind, Is.EqualTo(CodeSymbolKind.Method));
    }

    [Test]
    public async Task Extract_Python_ContainsClassAndFunction()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class MyClass:
                def my_method(self):
                    pass

            def top_level():
                pass
            """;

        var (symbols, _) = await ExtractFromCode(code, ".py");

        var cls = symbols.FirstOrDefault(s => s.Name == "MyClass");
        Assert.That(cls, Is.Not.Null);
        Assert.That(cls!.Kind, Is.EqualTo(CodeSymbolKind.Class));

        var method = symbols.FirstOrDefault(s => s.Name.StartsWith("my_method"));
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.Kind, Is.EqualTo(CodeSymbolKind.Method));

        var func = symbols.FirstOrDefault(s => s.Name.StartsWith("top_level"));
        Assert.That(func, Is.Not.Null);
        Assert.That(func!.Kind, Is.EqualTo(CodeSymbolKind.Function));
    }

    [Test]
    public async Task Extract_Python_NestedMethodHasFullName()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Outer:
                def inner_method(self):
                    pass
            """;

        var (symbols, _) = await ExtractFromCode(code, ".py");

        var method = symbols.FirstOrDefault(s => s.Name.StartsWith("inner_method"));
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.FullName, Is.EqualTo("Outer.inner_method(self)"));
    }

    [Test]
    public async Task Extract_Python_LineRangesAreValid()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Foo:
                def bar(self):
                    pass
            """;

        var (symbols, _) = await ExtractFromCode(code, ".py");

        foreach (var sym in symbols)
        {
            Assert.That(sym.LineRange.End, Is.GreaterThanOrEqualTo(sym.LineRange.Start),
                $"Symbol {sym.FullName} has invalid line range");
        }
    }

    [Test]
    public async Task Extract_TypeScript_LineRangesAreValid()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Foo {
                bar() {}
            }
            """;

        var (symbols, _) = await ExtractFromCode(code, ".ts");

        foreach (var symbol in symbols)
        {
            Assert.That(symbol.LineRange.End, Is.GreaterThanOrEqualTo(symbol.LineRange.Start),
                $"Symbol {symbol.FullName} has invalid line range");
        }
    }

    [Test]
    public async Task Extract_Go_ContainsTypeFunctionAndMethod()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            package main
            type Worker struct {}
            func TopLevel() {}
            func (w Worker) DoWork() {}
            """;

        var (symbols, _) = await ExtractFromCode(code, ".go");
        Assert.That(symbols.Any(s => s.Name == "Worker" && s.Kind == CodeSymbolKind.Class), Is.True);
        Assert.That(symbols.Any(s => s.Name.StartsWith("TopLevel") && s.Kind == CodeSymbolKind.Function), Is.True);
        Assert.That(symbols.Any(s => s.Name.StartsWith("DoWork") && s.Kind == CodeSymbolKind.Method), Is.True);
    }

    [Test]
    public async Task Extract_Rust_ContainsStructAndMethodInImpl()
    {
        Assume.That(IsTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            struct Worker;
            impl Worker {
                fn do_work(&self) {}
            }
            fn free_fn() {}
            """;

        var (symbols, _) = await ExtractFromCode(code, ".rs");
        Assert.That(symbols.Any(s => s.Name == "Worker" && s.Kind == CodeSymbolKind.Struct), Is.True);
        Assert.That(symbols.Any(s => s.Name.StartsWith("do_work") && s.Kind == CodeSymbolKind.Method), Is.True);
        Assert.That(symbols.Any(s => s.Name.StartsWith("free_fn") && s.Kind == CodeSymbolKind.Function), Is.True);
    }
}
