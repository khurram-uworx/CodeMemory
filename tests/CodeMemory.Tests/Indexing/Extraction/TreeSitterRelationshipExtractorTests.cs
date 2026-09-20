using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing.Extraction;

public sealed class TreeSitterRelationshipExtractorTests
{
    static readonly string tempDir = Path.GetTempPath();

    static bool isTreeSitterAvailable()
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

    static async Task<(IReadOnlyList<Symbol> Symbols, ParseResult Result, string FilePath)> extractFromCode(
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
            return (symbols, result!, path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ExtractRelationships_TypeScriptExtends_CreatesInherits()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class BaseClass {}
            class DerivedClass extends BaseClass {}
            """;

        var (symbols, result, path) = await extractFromCode(code, ".ts");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "DerivedClass" &&
            r.TargetSymbolId == "BaseClass" &&
            r.RelationshipType == "Inherits"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_TypeScriptImplements_CreatesImplements()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            interface MyInterface {}
            class MyClass implements MyInterface {}
            """;

        var (symbols, result, path) = await extractFromCode(code, ".ts");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "MyClass" &&
            r.TargetSymbolId == "MyInterface" &&
            r.RelationshipType == "Implements"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_TypeScriptMethodCall_CreatesCalls()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Callee {
                targetMethod() {}
            }
            class Caller {
                callIt() {
                    new Callee().targetMethod();
                }
            }
            """;

        var (symbols, result, path) = await extractFromCode(code, ".ts");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.RelationshipType == "Calls"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_TypeScriptTypeAnnotation_CreatesReferences()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class MyType {}
            class Consumer {
                ref: MyType;
            }
            """;

        var (symbols, result, path) = await extractFromCode(code, ".ts");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.RelationshipType == "References" &&
            r.TargetSymbolId == "MyType"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_JavaExtends_CreatesInherits()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Parent {}
            class Child extends Parent {}
            """;

        var (symbols, result, path) = await extractFromCode(code, ".java");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "Child" &&
            r.TargetSymbolId == "Parent" &&
            r.RelationshipType == "Inherits"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_PythonExtends_CreatesInherits()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class BaseClass:
                pass

            class DerivedClass(BaseClass):
                pass
            """;

        var (symbols, result, path) = await extractFromCode(code, ".py");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "DerivedClass" &&
            r.TargetSymbolId == "BaseClass" &&
            r.RelationshipType == "Inherits"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_PythonMethodCall_CreatesCalls()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Callee:
                def target_method(self):
                    pass

            class Caller:
                def call_it(self):
                    c = Callee()
                    c.target_method()
            """;

        var (symbols, result, path) = await extractFromCode(code, ".py");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.RelationshipType == "Calls"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_NoDuplicateRelationships()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Base {}
            class Derived extends Base {}
            """;

        var (symbols, result, path) = await extractFromCode(code, ".ts");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        var inheritsRels = relationships.Where(r => r.RelationshipType == "Inherits").ToList();
        Assert.That(inheritsRels, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ExtractRelationships_GoEmbeddedStruct_CreatesInherits()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            package main
            type Base struct {}
            type Derived struct {
                Base
            }
            """;

        var (symbols, result, path) = await extractFromCode(code, ".go");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "Derived" &&
            r.TargetSymbolId == "Base" &&
            r.RelationshipType == "Inherits"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_RustTraitBounds_CreatesInherits()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            trait Base {}
            trait Derived: Base {}
            """;

        var (symbols, result, path) = await extractFromCode(code, ".rs");
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "Derived" &&
            r.TargetSymbolId == "Base" &&
            r.RelationshipType == "Inherits"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_CppInheritance_CreatesInherits()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var code = """
            class Base {};
            class Derived : public Base {};
            """;

        var (symbols, result, path) = await extractFromCode(code, ".cpp");
        Assert.That(symbols.Any(s => s.Name == "Base"), Is.True, "Base symbol not extracted");
        Assert.That(symbols.Any(s => s.Name == "Derived"), Is.True, "Derived symbol not extracted");

        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(result, symbols, path);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "Derived" &&
            r.TargetSymbolId == "Base" &&
            r.RelationshipType == "Inherits"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_JavaImportedType_ResolvesViaImportMap()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var consumer = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            import uk.co.uworx.khoji.agile.internal.ServiceError;

            public class Consumer {
                private ServiceError error;
            }
            """, ".java");
        var internalError = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal;

            public class ServiceError {}
            """, ".java");
        var utilError = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.util;

            public class ServiceError {}
            """, ".java");

        // Two same-named classes exist; only the import disambiguates to the
        // internal.ServiceError one (Change 4 import-map step beats byName).
        var allSymbols = consumer.Symbols.Concat(internalError.Symbols).Concat(utilError.Symbols).ToList();
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(consumer.Result, allSymbols, consumer.FilePath);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "uk.co.uworx.khoji.agile.internal.error.Consumer.error" &&
            r.TargetSymbolId == "uk.co.uworx.khoji.agile.internal.ServiceError" &&
            r.RelationshipType == "References"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_JavaSamePackageType_PrefersSamePackageOverDuplicate()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var consumer = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class Consumer {
                private FieldError fieldError;
            }
            """, ".java");
        var errorFieldError = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class FieldError {}
            """, ".java");
        var internalFieldError = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal;

            public class FieldError {}
            """, ".java");

        var allSymbols = consumer.Symbols.Concat(errorFieldError.Symbols).Concat(internalFieldError.Symbols).ToList();
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(consumer.Result, allSymbols, consumer.FilePath);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "uk.co.uworx.khoji.agile.internal.error.Consumer.fieldError" &&
            r.TargetSymbolId == "uk.co.uworx.khoji.agile.internal.error.FieldError" &&
            r.RelationshipType == "References"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_JavaAmbiguousBareReference_SkipsEdge()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var consumer = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class Consumer {
                private BareThing thing;
            }
            """, ".java");
        var utilThing = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.util;

            public class BareThing {}
            """, ".java");
        var internalThing = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal;

            public class BareThing {}
            """, ".java");

        // Bare name with no import, not declared in the consumer's package:
        // two candidates, no context — the reference must be skipped, not
        // resolved to an arbitrary first match.
        var allSymbols = consumer.Symbols.Concat(utilThing.Symbols).Concat(internalThing.Symbols).ToList();
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(consumer.Result, allSymbols, consumer.FilePath);

        Assert.That(relationships.Any(r => r.TargetSymbolId.EndsWith("BareThing")), Is.False);
    }

    [Test]
    public async Task ExtractRelationships_JavaFullyQualifiedReference_ResolvesExactFullName()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var consumer = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class Consumer {
                private uk.co.uworx.khoji.agile.internal.ServiceError error;
            }
            """, ".java");
        var internalError = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal;

            public class ServiceError {}
            """, ".java");
        var utilError = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.util;

            public class ServiceError {}
            """, ".java");

        var allSymbols = consumer.Symbols.Concat(internalError.Symbols).Concat(utilError.Symbols).ToList();
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(consumer.Result, allSymbols, consumer.FilePath);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "uk.co.uworx.khoji.agile.internal.error.Consumer.error" &&
            r.TargetSymbolId == "uk.co.uworx.khoji.agile.internal.ServiceError" &&
            r.RelationshipType == "References"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_JavaSamePackageMethodCall_PrefersSamePackageOverload()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var consumer = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class Consumer {
                private OutThing thing;
                public void go() {
                    thing.ping();
                }
            }
            """, ".java");
        var errorThing = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class OutThing {
                public void ping() {}
            }
            """, ".java");
        var internalThing = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal;

            public class OutThing {
                public void ping() {}
            }
            """, ".java");

        var allSymbols = consumer.Symbols.Concat(errorThing.Symbols).Concat(internalThing.Symbols).ToList();
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(consumer.Result, allSymbols, consumer.FilePath);

        Assert.That(relationships.Any(r =>
            r.SourceSymbolId == "uk.co.uworx.khoji.agile.internal.error.Consumer.go()" &&
            r.TargetSymbolId == "uk.co.uworx.khoji.agile.internal.error.OutThing.ping()" &&
            r.RelationshipType == "Calls"), Is.True);
    }

    [Test]
    public async Task ExtractRelationships_JavaReceiverType_ResolvesDeclaredTypeMember()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var consumer = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class Consumer {
                private OutThing target;
                public void go() {
                    target.ping();
                    target.missing();
                }
            }
            """, ".java");
        var outThing = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class OutThing {
                public void ping() {}
            }
            """, ".java");
        var otherThing = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class OtherThing {
                public void ping() {}
            }
            """, ".java");

        var allSymbols = consumer.Symbols.Concat(outThing.Symbols).Concat(otherThing.Symbols).ToList();
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(consumer.Result, allSymbols, consumer.FilePath);

        var calls = relationships.Where(r => r.RelationshipType == "Calls").ToList();
        // Same package, same method name in two classes — the receiver's declared
        // type (OutThing) must disambiguate; `missing` is on no indexed type.
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].TargetSymbolId, Is.EqualTo(
            "uk.co.uworx.khoji.agile.internal.error.OutThing.ping()"));
    }

    [Test]
    public async Task ExtractRelationships_CrossLanguageNameCollision_SkipsCallsEdge()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var consumer = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class Consumer {
                public void go(ServiceError error) {
                    error.name();
                    capture();
                }
            }
            """, ".java");
        var tsModule = await extractFromCode("""
            const name = "x";
            function capture() {}
            """, ".ts");

        var allSymbols = consumer.Symbols.Concat(tsModule.Symbols).ToList();
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(consumer.Result, allSymbols, consumer.FilePath);

        // A TypeScript top-level `name`/`capture` must never satisfy a Java call:
        // bare names skip the byFullName fast path, and the by-name pool is
        // filtered to the source language before resolution.
        Assert.That(relationships.Any(r => r.RelationshipType == "Calls"), Is.False);
    }

    [Test]
    public async Task ExtractRelationships_JavaReceiverType_ImplicitEnumMethodSkipsEdge()
    {
        Assume.That(isTreeSitterAvailable(), "Tree-sitter native libraries not available");
        var consumer = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public class Consumer {
                public void go(ServiceError error) {
                    error.name();
                    error.getMessage();
                }
            }
            """, ".java");
        var serviceError = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.error;

            public enum ServiceError {
                SOME_ERROR;

                public String getMessage() { return ""; }
            }
            """, ".java");
        var otherWithName = await extractFromCode("""
            package uk.co.uworx.khoji.agile.internal.other;

            public class HasName {
                public String name;
            }
            """, ".java");

        var allSymbols = consumer.Symbols.Concat(serviceError.Symbols).Concat(otherWithName.Symbols).ToList();
        var extractor = new TreeSitterRelationshipExtractor(NullLogger<TreeSitterRelationshipExtractor>.Instance);
        var relationships = extractor.ExtractRelationships(consumer.Result, allSymbols, consumer.FilePath);

        // error.name() is the implicit enum method — not in the index → receiver
        // type narrows candidates to ServiceError members and finds none → skip.
        // error.getMessage() resolves to the enum's real member.
        Assert.That(relationships.Any(r =>
            r.TargetSymbolId == "uk.co.uworx.khoji.agile.internal.error.ServiceError.getMessage()" &&
            r.RelationshipType == "Calls"), Is.True);
        Assert.That(relationships.Any(r =>
            r.TargetSymbolId?.Contains("HasName.name", StringComparison.Ordinal) == true), Is.False);
    }
}
