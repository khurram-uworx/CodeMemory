using CodeMemory.Indexing.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace CodeMemory.Indexing.Extraction;

public sealed class RoslynRelationshipExtractor : IRelationshipExtractor
{
    static string? extractIdentifier(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            QualifiedNameSyntax qn => qn.ToString(),
            GenericNameSyntax gn => gn.Identifier.Text,
            NullableTypeSyntax nt => extractIdentifier(nt.ElementType),
            ArrayTypeSyntax at => extractIdentifier(at.ElementType),
            _ => null,
        };
    }

    static string relationshipId(string source, string target, string type)
    {
        return $"{source}->{target}:{type}";
    }

    static Symbol? findContainingSymbol(SyntaxNode node, IReadOnlyList<Symbol> symbols, string filePath)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        var line = lineSpan.StartLinePosition.Line + 1;

        return symbols
            .Where(s => s.FilePath == filePath && s.LineRange.Start <= line && s.LineRange.End >= line)
            .OrderBy(s => s.LineRange.End - s.LineRange.Start)
            .FirstOrDefault();
    }

    static Symbol? findSymbolByName(string name,
        ILookup<string, Symbol> byName, ILookup<string, Symbol> byFullName)
    {
        if (primitiveTypes.Contains(name))
            return null;

        if (byFullName.Contains(name))
            return byFullName[name].First();

        if (byName.Contains(name))
            return byName[name].First();

        var withParens = $"{name}()";
        if (byName.Contains(withParens))
            return byName[withParens].First();

        foreach (var entry in byName)
        {
            if (entry.Key.StartsWith(name + "(", StringComparison.Ordinal))
                return entry.First();
        }

        return null;
    }

    static readonly HashSet<string> primitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
        "float", "double", "decimal", "bool", "char", "string", "object",
        "void", "var", "dynamic", "nint", "nuint"
    };

    readonly ILogger<RoslynRelationshipExtractor> logger;

    public RoslynRelationshipExtractor(ILogger<RoslynRelationshipExtractor> logger)
    {
        this.logger = logger;
    }


    void addRelationship(string sourceId, string targetId, string type,
        HashSet<string> seen, List<Relationship> results)
    {
        if (seen.Add(relationshipId(sourceId, targetId, type)))
        {
            results.Add(new Relationship(sourceId, targetId, type));
            logger.LogDebug("Relationship: {Source} --[{Type}]--> {Target}",
                sourceId, type, targetId);
        }
    }

    Symbol? resolveSourceForNode(SyntaxNode node, IReadOnlyList<Symbol> symbols, string filePath)
    {
        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        return member != null ? findContainingSymbol(member, symbols, filePath) : null;
    }

    Symbol? resolveTargetForType(TypeSyntax type, ILookup<string, Symbol> byName, ILookup<string, Symbol> byFullName)
    {
        var typeName = extractIdentifier(type);
        if (typeName == null || primitiveTypes.Contains(typeName))
            return null;

        return findSymbolByName(typeName, byName, byFullName);
    }

    public IReadOnlyList<Relationship> ExtractRelationships(
        ParseResult result, IReadOnlyList<Symbol> symbols, string filePath)
    {
        return ExtractRelationships(result.RoslynTree!, symbols, filePath);
    }
    public IReadOnlyList<Relationship> ExtractRelationships(
        SyntaxTree syntaxTree, IReadOnlyList<Symbol> symbols, string filePath)
    {
        var root = syntaxTree.GetRoot();
        var results = new List<Relationship>();
        var seen = new HashSet<string>();

        var byName = symbols.ToLookup(s => s.Name);
        var byFullName = symbols.ToLookup(s => s.FullName);

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (typeDecl.BaseList == null)
                continue;

            var sourceSymbol = findContainingSymbol(typeDecl, symbols, filePath);
            if (sourceSymbol == null)
                continue;

            foreach (var baseType in typeDecl.BaseList.Types)
            {
                var target = resolveTargetForType(baseType.Type, byName, byFullName);
                if (target == null)
                    continue;

                var relType = target.Kind == CodeSymbolKind.Interface ? "Implements" : "Inherits";
                addRelationship(sourceSymbol.FullName, target.FullName, relType, seen, results);
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var source = resolveSourceForNode(invocation, symbols, filePath);
            if (source == null)
                continue;

            var methodName = invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _ => null,
            };

            if (methodName == null || primitiveTypes.Contains(methodName))
                continue;

            var target = findSymbolByName(methodName, byName, byFullName);
            if (target == null)
                continue;

            addRelationship(source.FullName, target.FullName, "Calls", seen, results);
        }

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var source = resolveSourceForNode(creation, symbols, filePath);
            if (source == null)
                continue;

            var target = resolveTargetForType(creation.Type, byName, byFullName);
            if (target == null)
                continue;

            addRelationship(source.FullName, target.FullName, "References", seen, results);
        }

        foreach (var variable in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            var source = resolveSourceForNode(variable, symbols, filePath);
            if (source == null)
                continue;

            var target = resolveTargetForType(variable.Type, byName, byFullName);
            if (target == null)
                continue;

            addRelationship(source.FullName, target.FullName, "References", seen, results);
        }

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            var source = findContainingSymbol(property, symbols, filePath);
            if (source == null)
                continue;

            var target = resolveTargetForType(property.Type, byName, byFullName);
            if (target == null)
                continue;

            addRelationship(source.FullName, target.FullName, "References", seen, results);
        }

        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Type == null)
                continue;

            var source = resolveSourceForNode(parameter, symbols, filePath);
            if (source == null)
                continue;

            var target = resolveTargetForType(parameter.Type, byName, byFullName);
            if (target == null)
                continue;

            addRelationship(source.FullName, target.FullName, "References", seen, results);
        }

        logger.LogDebug("Extracted {Count} relationships from {File}", results.Count, filePath);
        return results;
    }
}
