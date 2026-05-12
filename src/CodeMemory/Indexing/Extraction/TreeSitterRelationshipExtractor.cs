using CodeMemory.Indexing.Parsing;
using Microsoft.Extensions.Logging;
using TreeSitter;

namespace CodeMemory.Indexing.Extraction;

public sealed class TreeSitterRelationshipExtractor : IRelationshipExtractor
{
    static string relationshipId(string source, string target, string type)
    {
        return $"{source}->{target}:{type}";
    }

    static Symbol? findContainingSymbol(Node node, IReadOnlyList<Symbol> symbols, string filePath)
    {
        var line = node.StartPosition.Row + 1;
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

    static string? extractTypeName(Node node)
    {
        return node.Type switch
        {
            "type_identifier" or "identifier" => node.Text,
            "predefined_type" => node.Text,
            "scoped_type_identifier" => node.Text,
            "generic_type" => node.NamedChildren.FirstOrDefault()?.Text,
            "array_type" => node.NamedChildren is { Count: > 0 } aChild ? extractTypeName(aChild[0]) : null,
            _ => null,
        };
    }

    static readonly HashSet<string> primitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
        "float", "double", "decimal", "bool", "char", "string", "object",
        "void", "var", "dynamic", "nint", "nuint",
        "number", "boolean", "symbol", "null", "undefined", "any", "never", "unknown",
        "String", "Integer", "Boolean", "Character", "Byte", "Short", "Long",
        "Float", "Double", "Void",
    };

    readonly ILogger<TreeSitterRelationshipExtractor> logger;

    public TreeSitterRelationshipExtractor(ILogger<TreeSitterRelationshipExtractor> logger)
    {
        this.logger = logger;
    }

    void walkTree(Node node,
        IReadOnlyList<Symbol> symbols,
        ILookup<string, Symbol> byName,
        ILookup<string, Symbol> byFullName,
        string filePath,
        Parsing.Language language,
        HashSet<string> seen,
        List<Relationship> results)
    {
        if (node == default) return;

        switch (node.Type)
        {
            case "class_declaration":
            case "abstract_class_declaration":
            case "interface_declaration":
                processHeritage(node, symbols, byName, byFullName, filePath, language, seen, results);
                break;
            case "call_expression":
                processCall(node, symbols, byName, byFullName, filePath, seen, results);
                break;
            case "method_invocation":
                processMethodInvocation(node, symbols, byName, byFullName, filePath, seen, results);
                break;
            case "new_expression":
            case "object_creation":
                processObjectCreation(node, symbols, byName, byFullName, filePath, seen, results);
                break;
        }

        checkTypeAnnotation(node, symbols, byName, byFullName, filePath, seen, results);

        foreach (var child in node.NamedChildren)
        {
            walkTree(child, symbols, byName, byFullName, filePath, language, seen, results);
        }
    }

    void processHeritageClause(Node clause, Symbol source, string relType,
        ILookup<string, Symbol> byName, ILookup<string, Symbol> byFullName,
        HashSet<string> seen, List<Relationship> results)
    {
        foreach (var typeChild in clause.NamedChildren)
        {
            var typeName = extractTypeName(typeChild);
            if (typeName == null || primitiveTypes.Contains(typeName)) continue;
            var target = findSymbolByName(typeName, byName, byFullName);
            if (target == null || target.FullName == source.FullName) continue;
            addRelationship(source.FullName, target.FullName, relType, seen, results);
        }
    }

    void processHeritage(Node node,
        IReadOnlyList<Symbol> symbols,
        ILookup<string, Symbol> byName,
        ILookup<string, Symbol> byFullName,
        string filePath,
        Parsing.Language language,
        HashSet<string> seen,
        List<Relationship> results)
    {
        var source = findContainingSymbol(node, symbols, filePath);
        if (source == null) return;

        foreach (var child in node.NamedChildren)
        {
            // TypeScript nests extends/implements under class_heritage
            if (child.Type == "class_heritage")
            {
                foreach (var sub in child.NamedChildren)
                    processNestedHeritageClause(sub, source, byName, byFullName, seen, results);
                continue;
            }

            if (child.Type is "heritage_clause" or "extends_clause" or "superclass")
            {
                processHeritageClause(child, source, "Inherits", byName, byFullName, seen, results);
            }

            if (child.Type == "implements_clause")
            {
                processHeritageClause(child, source, "Implements", byName, byFullName, seen, results);
            }
        }

        if (language == Parsing.Language.Java)
        {
            var superField = node.Fields.FirstOrDefault(f => f.Key == "superclass");
            if (superField.Key != null)
            {
                var typeName = extractTypeName(superField.Value);
                if (typeName != null && !primitiveTypes.Contains(typeName))
                {
                    var target = findSymbolByName(typeName, byName, byFullName);
                    if (target != null && target.FullName != source.FullName)
                        addRelationship(source.FullName, target.FullName, "Inherits", seen, results);
                }
            }

            var interfacesField = node.Fields.FirstOrDefault(f => f.Key == "interfaces");
            if (interfacesField.Key != null)
            {
                collectTypeRefs(interfacesField.Value, source, "Implements", byName, byFullName, seen, results);
            }

            if (node.Type == "interface_declaration")
            {
                var extendsField = node.Fields.FirstOrDefault(f => f.Key == "extends");
                if (extendsField.Key != null)
                {
                    collectTypeRefs(extendsField.Value, source, "Inherits", byName, byFullName, seen, results);
                }
            }
        }
    }

    void processNestedHeritageClause(Node clause, Symbol source,
        ILookup<string, Symbol> byName, ILookup<string, Symbol> byFullName,
        HashSet<string> seen, List<Relationship> results)
    {
        if (clause.Type == "extends_clause")
            processHeritageClause(clause, source, "Inherits", byName, byFullName, seen, results);
        else if (clause.Type == "implements_clause")
            processHeritageClause(clause, source, "Implements", byName, byFullName, seen, results);
        else
            processHeritageClause(clause, source, "Inherits", byName, byFullName, seen, results);
    }

    void collectTypeRefs(Node typeListNode, Symbol source, string relType,
        ILookup<string, Symbol> byName, ILookup<string, Symbol> byFullName,
        HashSet<string> seen, List<Relationship> results)
    {
        if (typeListNode.Type == "type_list")
        {
            foreach (var child in typeListNode.NamedChildren)
            {
                var typeName = extractTypeName(child);
                if (typeName == null || primitiveTypes.Contains(typeName)) continue;
                var target = findSymbolByName(typeName, byName, byFullName);
                if (target == null || target.FullName == source.FullName) continue;
                addRelationship(source.FullName, target.FullName, relType, seen, results);
            }
        }
        else
        {
            var typeName = extractTypeName(typeListNode);
            if (typeName != null && !primitiveTypes.Contains(typeName))
            {
                var target = findSymbolByName(typeName, byName, byFullName);
                if (target != null && target.FullName != source.FullName)
                    addRelationship(source.FullName, target.FullName, relType, seen, results);
            }
        }
    }

    void processCall(Node node,
        IReadOnlyList<Symbol> symbols,
        ILookup<string, Symbol> byName,
        ILookup<string, Symbol> byFullName,
        string filePath,
        HashSet<string> seen,
        List<Relationship> results)
    {
        var source = findContainingSymbol(node, symbols, filePath);
        if (source == null) return;

        var funcField = node.Fields.FirstOrDefault(f => f.Key == "function");
        if (funcField.Key == null) return;

        var funcNode = funcField.Value;
        string? methodName = null;

        if (funcNode.Type is "identifier" or "property_identifier")
        {
            methodName = funcNode.Text;
        }
        else if (funcNode.Type == "member_expression")
        {
            var propField = funcNode.Fields.FirstOrDefault(f => f.Key == "property");
            if (propField.Key != null)
                methodName = propField.Value.Text;
        }

        if (methodName == null || primitiveTypes.Contains(methodName))
            return;

        var target = findSymbolByName(methodName, byName, byFullName);
        if (target == null || target.FullName == source.FullName)
            return;

        addRelationship(source.FullName, target.FullName, "Calls", seen, results);
    }

    void processMethodInvocation(Node node,
        IReadOnlyList<Symbol> symbols,
        ILookup<string, Symbol> byName,
        ILookup<string, Symbol> byFullName,
        string filePath,
        HashSet<string> seen,
        List<Relationship> results)
    {
        var source = findContainingSymbol(node, symbols, filePath);
        if (source == null) return;

        var nameField = node.Fields.FirstOrDefault(f => f.Key == "name");
        if (nameField.Key == null) return;

        var methodName = nameField.Value.Text;
        if (primitiveTypes.Contains(methodName))
            return;

        var target = findSymbolByName(methodName, byName, byFullName);
        if (target == null || target.FullName == source.FullName)
            return;

        addRelationship(source.FullName, target.FullName, "Calls", seen, results);
    }

    void processObjectCreation(Node node,
        IReadOnlyList<Symbol> symbols,
        ILookup<string, Symbol> byName,
        ILookup<string, Symbol> byFullName,
        string filePath,
        HashSet<string> seen,
        List<Relationship> results)
    {
        var source = findContainingSymbol(node, symbols, filePath);
        if (source == null) return;

        var typeField = node.Fields.FirstOrDefault(f => f.Key is "constructor" or "type");
        if (typeField.Key == null) return;

        var typeName = extractTypeName(typeField.Value);
        if (typeName == null || primitiveTypes.Contains(typeName))
            return;

        var target = findSymbolByName(typeName, byName, byFullName);
        if (target == null || target.FullName == source.FullName)
            return;

        addRelationship(source.FullName, target.FullName, "References", seen, results);
    }

    void checkTypeAnnotation(Node node,
        IReadOnlyList<Symbol> symbols,
        ILookup<string, Symbol> byName,
        ILookup<string, Symbol> byFullName,
        string filePath,
        HashSet<string> seen,
        List<Relationship> results)
    {
        var typeField = node.Fields.FirstOrDefault(f => f.Key == "type");
        if (typeField.Key == null) return;

        var source = findContainingSymbol(node, symbols, filePath);
        if (source == null) return;

        string? typeName;
        if (typeField.Value.Type == "type_annotation")
        {
            var typeExpr = typeField.Value.NamedChildren.FirstOrDefault();
            if (typeExpr == default) return;
            typeName = extractTypeName(typeExpr);
        }
        else
        {
            typeName = extractTypeName(typeField.Value);
        }

        if (typeName == null || primitiveTypes.Contains(typeName))
            return;

        var target = findSymbolByName(typeName, byName, byFullName);
        if (target == null || target.FullName == source.FullName)
            return;

        addRelationship(source.FullName, target.FullName, "References", seen, results);
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

    public IReadOnlyList<Relationship> ExtractRelationships(
        ParseResult result, IReadOnlyList<Symbol> symbols, string filePath)
    {
        if (result.TsTree is not Tree tree)
            return Array.Empty<Relationship>();

        var byName = symbols.ToLookup(s => s.Name);
        var byFullName = symbols.ToLookup(s => s.FullName);
        var results = new List<Relationship>();
        var seen = new HashSet<string>();

        walkTree(tree.RootNode, symbols, byName, byFullName, filePath, result.Language, seen, results);

        logger.LogDebug("Extracted {Count} relationships from {File}", results.Count, filePath);
        return results;
    }
}
