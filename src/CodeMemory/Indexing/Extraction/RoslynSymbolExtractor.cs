using CodeMemory.Indexing.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace CodeMemory.Indexing.Extraction;

public sealed class RoslynSymbolExtractor : ISymbolExtractor
{
    static void extractType(
        TypeDeclarationSyntax typeDecl,
        string filePath,
        string? parentFullName,
        List<Symbol> symbols)
    {
        var kind = typeDecl switch
        {
            InterfaceDeclarationSyntax => CodeSymbolKind.Interface,
            StructDeclarationSyntax => CodeSymbolKind.Struct,
            RecordDeclarationSyntax => CodeSymbolKind.Record,
            _ => CodeSymbolKind.Class,
        };

        var name = typeDecl.Identifier.Text;
        if (typeDecl.TypeParameterList != null)
        {
            var paramsList = string.Join(", ", typeDecl.TypeParameterList.Parameters.Select(p => p.Identifier.Text));
            name = $"{name}<{paramsList}>";
        }

        var fullName = parentFullName != null ? $"{parentFullName}.{name}" : name;
        var lineSpan = typeDecl.GetLocation().GetLineSpan();
        var modifiers = typeDecl.Modifiers.Select(m => m.Text).ToList();
        var documentation = getDocumentationFromTrivia(typeDecl);

        symbols.Add(new Symbol(
            typeDecl.Identifier.Text,
            kind,
            filePath,
            new LineRange(lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1),
            fullName,
            modifiers,
            documentation));

        // Walk direct child members (for nested types, methods, etc.)
        foreach (var childMember in typeDecl.Members)
            extractFromMember(childMember, filePath, fullName, symbols);
    }

    static void extractEnum(
        EnumDeclarationSyntax enumDecl,
        string filePath,
        string? parentFullName,
        List<Symbol> symbols)
    {
        var fullName = parentFullName != null ? $"{parentFullName}.{enumDecl.Identifier.Text}" : enumDecl.Identifier.Text;
        var lineSpan = enumDecl.GetLocation().GetLineSpan();
        var modifiers = enumDecl.Modifiers.Select(m => m.Text).ToList();
        var documentation = getDocumentationFromTrivia(enumDecl);

        symbols.Add(new Symbol(
            enumDecl.Identifier.Text,
            CodeSymbolKind.Enum,
            filePath,
            new LineRange(lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1),
            fullName,
            modifiers,
            documentation));
    }

    static void extractMethod(
        MethodDeclarationSyntax methodDecl,
        string filePath,
        string? parentFullName,
        List<Symbol> symbols)
    {
        var name = buildMethodName(methodDecl);
        var fullName = parentFullName != null ? $"{parentFullName}.{name}" : name;
        var lineSpan = methodDecl.GetLocation().GetLineSpan();
        var modifiers = methodDecl.Modifiers.Select(m => m.Text).ToList();
        var documentation = getDocumentationFromTrivia(methodDecl);

        symbols.Add(new Symbol(
            name,
            CodeSymbolKind.Method,
            filePath,
            new LineRange(lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1),
            fullName,
            modifiers,
            documentation));
    }

    static void extractConstructor(
        ConstructorDeclarationSyntax ctorDecl,
        string filePath,
        string? parentFullName,
        List<Symbol> symbols)
    {
        var name = buildMethodName(ctorDecl);
        var parentName = parentFullName ?? ctorDecl.Identifier.Text;
        var fullName = $"{parentName}.{name}";
        var lineSpan = ctorDecl.GetLocation().GetLineSpan();
        var modifiers = ctorDecl.Modifiers.Select(m => m.Text).ToList();
        var documentation = getDocumentationFromTrivia(ctorDecl);

        symbols.Add(new Symbol(
            name,
            CodeSymbolKind.Method,
            filePath,
            new LineRange(lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1),
            fullName,
            modifiers,
            documentation));
    }

    static void extractProperty(
        PropertyDeclarationSyntax propDecl,
        string filePath,
        string? parentFullName,
        List<Symbol> symbols)
    {
        var fullName = parentFullName != null ? $"{parentFullName}.{propDecl.Identifier.Text}" : propDecl.Identifier.Text;
        var lineSpan = propDecl.GetLocation().GetLineSpan();
        var modifiers = propDecl.Modifiers.Select(m => m.Text).ToList();
        var documentation = getDocumentationFromTrivia(propDecl);

        symbols.Add(new Symbol(
            propDecl.Identifier.Text,
            CodeSymbolKind.Property,
            filePath,
            new LineRange(lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1),
            fullName,
            modifiers,
            documentation));
    }

    static void extractField(
        FieldDeclarationSyntax fieldDecl,
        string filePath,
        string? parentFullName,
        List<Symbol> symbols)
    {
        var modifiers = fieldDecl.Modifiers.Select(m => m.Text).ToList();

        foreach (var variable in fieldDecl.Declaration.Variables)
        {
            var fullName = parentFullName != null ? $"{parentFullName}.{variable.Identifier.Text}" : variable.Identifier.Text;
            var lineSpan = variable.GetLocation().GetLineSpan();

            symbols.Add(new Symbol(
                variable.Identifier.Text,
                CodeSymbolKind.Field,
                filePath,
                new LineRange(lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1),
                fullName,
                modifiers));
        }
    }

    static void extractEvent(
        EventDeclarationSyntax eventDecl,
        string filePath,
        string? parentFullName,
        List<Symbol> symbols)
    {
        var fullName = parentFullName != null ? $"{parentFullName}.{eventDecl.Identifier.Text}" : eventDecl.Identifier.Text;
        var lineSpan = eventDecl.GetLocation().GetLineSpan();
        var modifiers = eventDecl.Modifiers.Select(m => m.Text).ToList();
        var documentation = getDocumentationFromTrivia(eventDecl);

        symbols.Add(new Symbol(
            eventDecl.Identifier.Text,
            CodeSymbolKind.Event,
            filePath,
            new LineRange(lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1),
            fullName,
            modifiers,
            documentation));
    }

    static void extractEventField(
        EventFieldDeclarationSyntax eventFieldDecl,
        string filePath,
        string? parentFullName,
        List<Symbol> symbols)
    {
        var modifiers = eventFieldDecl.Modifiers.Select(m => m.Text).ToList();

        foreach (var variable in eventFieldDecl.Declaration.Variables)
        {
            var fullName = parentFullName != null ? $"{parentFullName}.{variable.Identifier.Text}" : variable.Identifier.Text;
            var lineSpan = variable.GetLocation().GetLineSpan();

            symbols.Add(new Symbol(
                variable.Identifier.Text,
                CodeSymbolKind.Event,
                filePath,
                new LineRange(lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1),
                fullName,
                modifiers));
        }
    }

    static void extractFromMember(MemberDeclarationSyntax member, string filePath,
        string? parentFullName,
        List<Symbol> symbols)
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax nsDecl:
                foreach (var nsMember in nsDecl.Members)
                    extractFromMember(nsMember, filePath, parentFullName, symbols);
                break;

            case EnumDeclarationSyntax enumDecl:
                extractEnum(enumDecl, filePath, parentFullName, symbols);
                break;

            case TypeDeclarationSyntax typeDecl:
                extractType(typeDecl, filePath, parentFullName, symbols);
                break;

            case MethodDeclarationSyntax methodDecl:
                extractMethod(methodDecl, filePath, parentFullName, symbols);
                break;

            case ConstructorDeclarationSyntax ctorDecl:
                extractConstructor(ctorDecl, filePath, parentFullName, symbols);
                break;

            case PropertyDeclarationSyntax propDecl:
                extractProperty(propDecl, filePath, parentFullName, symbols);
                break;

            case FieldDeclarationSyntax fieldDecl:
                extractField(fieldDecl, filePath, parentFullName, symbols);
                break;

            case EventDeclarationSyntax eventDecl:
                extractEvent(eventDecl, filePath, parentFullName, symbols);
                break;

            case EventFieldDeclarationSyntax eventFieldDecl:
                extractEventField(eventFieldDecl, filePath, parentFullName, symbols);
                break;
        }
    }

    static string buildMethodName(BaseMethodDeclarationSyntax methodDecl)
    {
        var name = methodDecl switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            _ => "unknown",
        };

        if (methodDecl is MethodDeclarationSyntax method && method.TypeParameterList != null)
        {
            var typeParams = string.Join(", ", method.TypeParameterList.Parameters.Select(p => p.Identifier.Text));
            name = $"{name}<{typeParams}>";
        }

        var parameters = string.Join(", ",
            methodDecl.ParameterList.Parameters.Select(p =>
            {
                var typeName = p.Type?.ToString() ?? "var";
                return $"{typeName} {p.Identifier.Text}";
            }));

        return $"{name}({parameters})";
    }

    static string? getDocumentationFromTrivia(SyntaxNode node)
    {
        var leadingTrivia = node.GetLeadingTrivia();
        var docComment = leadingTrivia
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));

        if (!docComment.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            return null;

        var xml = docComment.GetStructure() as DocumentationCommentTriviaSyntax;
        if (xml == null)
            return null;

        var summary = xml.Content
            .OfType<XmlElementSyntax>()
            .FirstOrDefault(e => e.StartTag.Name.ToString() == "summary");

        if (summary == null)
            return null;

        return summary.Content.ToFullString().Trim();
    }

    readonly ILogger<RoslynSymbolExtractor> logger;

    public RoslynSymbolExtractor(ILogger<RoslynSymbolExtractor> logger)
        => (this.logger) = (logger);

    // internal because of tests
    internal IReadOnlyList<Symbol> Extract(SyntaxTree syntaxTree, string filePath)
    {
        var root = syntaxTree.GetRoot();
        var symbols = new List<Symbol>();

        foreach (var member in root.ChildNodes().OfType<MemberDeclarationSyntax>())
            extractFromMember(member, filePath, parentFullName: null, symbols);

        logger.LogDebug("Extracted {Count} symbols from {File}", symbols.Count, filePath);
        return symbols;
    }

    public IReadOnlyList<Symbol> Extract(ParseResult result, string filePath)
        => Extract(result.RoslynTree!, filePath);
}
