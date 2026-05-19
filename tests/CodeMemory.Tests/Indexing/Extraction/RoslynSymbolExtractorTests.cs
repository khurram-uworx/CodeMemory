using CodeMemory.Indexing.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Indexing.Extraction;

public sealed class RoslynSymbolExtractorTests
{
    static readonly string fixturesDir = Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..",
        "CodeMemory.Tests", "fixtures");

    static SyntaxTree parseFixture(string fileName)
    {
        var path = Path.Combine(fixturesDir, fileName);
        var text = File.ReadAllText(path);
        return CSharpSyntaxTree.ParseText(text, path: path);
    }

    [Test]
    public void Extract_WithSampleClass_ReturnsExpectedSymbols()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");
        var filePath = syntaxTree.FilePath;

        // SampleClass.cs expected symbols:
        // SampleClass (class), Name (property), Add (method), DoNothing (method),
        // FieldExample (field), MyEvent (event)
        // IGenericRepository<T> (interface), GetById (method), Save (method)
        // Point (struct), X (property), Y (property)
        // Status (enum)
        // OuterClass (class), InnerClass (class), InnerMethod (method), InnerEnum (enum)
        // AbstractBase (class), AbstractMethod (method)
        // UtilityClass (class -- static), Format (method)
        // Person (record)
        // GenericMethodClass (class), Echo<T> (method)

        var symbols = extractor.Extract(syntaxTree, filePath);

        Assert.That(symbols, Is.Not.Empty);

        // Verify type count (including nested types)
        var types = symbols.Where(s => s.Kind is CodeSymbolKind.Class or CodeSymbolKind.Interface
            or CodeSymbolKind.Struct or CodeSymbolKind.Enum or CodeSymbolKind.Record).ToList();
        Assert.That(types.Count, Is.EqualTo(11));
    }

    [Test]
    public void Extract_WithSampleClass_ContainsSampleClassSymbol()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);

        var sampleClass = symbols.FirstOrDefault(s => s.Name == "SampleClass");
        Assert.That(sampleClass, Is.Not.Null);
        Assert.That(sampleClass!.Kind, Is.EqualTo(CodeSymbolKind.Class));
        Assert.That(sampleClass.Modifiers, Does.Contain("public"));
    }

    [Test]
    public void Extract_WithSampleClass_ContainsMethods()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);

        Assert.That(symbols.Any(s => s.Name.StartsWith("Add(")), Is.True);
        Assert.That(symbols.Any(s => s.Name.StartsWith("DoNothing(")), Is.True);
    }

    [Test]
    public void Extract_WithSampleClass_ContainsProperty()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);

        var nameProp = symbols.FirstOrDefault(s => s.Name == "Name");
        Assert.That(nameProp, Is.Not.Null);
        Assert.That(nameProp!.Kind, Is.EqualTo(CodeSymbolKind.Property));
    }

    [Test]
    public void Extract_WithSampleClass_ContainsNestedTypesWithQualifiedNames()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);

        var innerClass = symbols.FirstOrDefault(s => s.Name == "InnerClass");
        Assert.That(innerClass, Is.Not.Null);
        Assert.That(innerClass!.FullName, Is.EqualTo("CodeMemory.Tests.Fixtures.OuterClass.InnerClass"));

        var innerMethod = symbols.FirstOrDefault(s => s.FullName == "CodeMemory.Tests.Fixtures.OuterClass.InnerClass.InnerMethod()");
        Assert.That(innerMethod, Is.Not.Null);
        Assert.That(innerMethod!.Kind, Is.EqualTo(CodeSymbolKind.Method));
    }

    [Test]
    public void Extract_WithSampleClass_GenericMethodIncludesTypeParameters()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);

        var echoMethod = symbols.FirstOrDefault(s => s.FullName.Contains("Echo"));
        Assert.That(echoMethod, Is.Not.Null);
        Assert.That(echoMethod!.Name, Does.Contain("<T>"));
        Assert.That(echoMethod.FullName, Does.Contain("<T>"));
    }

    [Test]
    public void Extract_WithSampleClass_ContainsDocumentation()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);
        var sampleClass = symbols.First(s => s.Name == "SampleClass");

        Assert.That(!string.IsNullOrEmpty(sampleClass.Documentation));
        Assert.That(sampleClass.Documentation, Does.Contain("sample class"));
    }

    [Test]
    public void Extract_WithSampleClass_ContainsInterfaceAndMethods()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);

        var iface = symbols.FirstOrDefault(s => s.Name == "IGenericRepository");
        Assert.That(iface, Is.Not.Null);
        Assert.That(iface!.Kind, Is.EqualTo(CodeSymbolKind.Interface));

        var getById = symbols.FirstOrDefault(s => s.FullName.Contains("GetById"));
        Assert.That(getById, Is.Not.Null);
    }

    [Test]
    public void Extract_WithSampleClass_ContainsStruct()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);

        var point = symbols.FirstOrDefault(s => s.Name == "Point");
        Assert.That(point, Is.Not.Null);
        Assert.That(point!.Kind, Is.EqualTo(CodeSymbolKind.Struct));
    }

    [Test]
    public void Extract_WithSampleClass_ContainsEnum()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);

        var status = symbols.FirstOrDefault(s => s.Name == "Status");
        Assert.That(status, Is.Not.Null);
        Assert.That(status!.Kind, Is.EqualTo(CodeSymbolKind.Enum));
    }

    [Test]
    public void Extract_WithSampleClass_ContainsRecord()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);

        var person = symbols.FirstOrDefault(s => s.Name == "Person");
        Assert.That(person, Is.Not.Null);
        Assert.That(person!.Kind, Is.EqualTo(CodeSymbolKind.Record));
    }

    [Test]
    public void Extract_WithSampleClass_LineRangesAreValid()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = parseFixture("SampleClass.cs");

        var symbols = extractor.Extract(syntaxTree, syntaxTree.FilePath);

        foreach (var symbol in symbols)
        {
            Assert.That(symbol.LineRange.End, Is.GreaterThanOrEqualTo(symbol.LineRange.Start),
                $"Symbol {symbol.FullName} has invalid line range");
        }
    }

    [Test]
    public void Extract_WithEmptyFile_ReturnsEmpty()
    {
        var extractor = new RoslynSymbolExtractor(NullLogger<RoslynSymbolExtractor>.Instance);
        var syntaxTree = CSharpSyntaxTree.ParseText("// just a comment", path: "empty.cs");

        var symbols = extractor.Extract(syntaxTree, "empty.cs");

        Assert.That(symbols, Is.Empty);
    }
}
