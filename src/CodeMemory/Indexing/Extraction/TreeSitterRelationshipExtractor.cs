using CodeMemory.Indexing.Graph;
using CodeMemory.Indexing.Parsing;
using Microsoft.Extensions.Logging;
using TreeSitter;

namespace CodeMemory.Indexing.Extraction;

public sealed class TreeSitterRelationshipExtractor : IRelationshipExtractor
{
    static string relationshipId(string source, string target, string type)
        => $"{source}->{target}:{type}";

    static Symbol? findContainingSymbol(Node node, ResolutionContext ctx)
    {
        var line = node.StartPosition.Row + 1;
        return ctx.Symbols
            .Where(s => s.FilePath == ctx.FilePath && s.LineRange.Start <= line && s.LineRange.End >= line)
            .OrderBy(s => s.LineRange.End - s.LineRange.Start)
            .FirstOrDefault();
    }

    /// <summary>
    /// Per-file context for deterministic reference resolution. Java supplies a
    /// package prefix, an explicit-import map (simple → qualified) and wildcard
    /// package prefixes; TypeScript supplies none (module specifiers cannot be
    /// expanded to index FullNames without module resolution — documented
    /// limitation of Change 4).
    /// </summary>
    sealed record ResolutionContext(
        string FilePath,
        Parsing.Language Language,
        string? PackagePrefix,
        IReadOnlyDictionary<string, string> Imports,
        IReadOnlyList<string> WildcardPackages,
        IReadOnlyList<Symbol> Symbols,
        ILookup<string, Symbol> ByName,
        ILookup<string, Symbol> ByFullName);

    /// <summary>
    /// Resolves a reference to a symbol with a deterministic precedence and no
    /// arbitrary first match (parity with storage-side resolution, #131 Change 2):
    /// fully-qualified name → explicit import → same file → same package →
    /// wildcard-imported package → unique name overall → null (edge skipped).
    ///
    /// Method overloads of a single declaring type resolve to the first match —
    /// the same documented tradeoff as the storage signature-prefix step.
    /// </summary>
    static Symbol? resolveSymbol(string name, ResolutionContext ctx)
    {
        if (primitiveTypes.Contains(name))
            return null;

        if (ctx.ByFullName.Contains(name))
            return ctx.ByFullName[name].First();

        if (ctx.Imports.TryGetValue(name, out var qualified) && ctx.ByFullName.Contains(qualified))
            return ctx.ByFullName[qualified].First();

        var candidates = collectCandidates(name, ctx);
        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        var resolved = preferFamily(candidates.Where(c => c.FilePath == ctx.FilePath).ToList());
        if (resolved != null)
            return resolved;

        if (ctx.PackagePrefix != null)
        {
            var prefix = ctx.PackagePrefix + ".";
            resolved = preferFamily(candidates
                .Where(c => c.FullName.StartsWith(prefix, StringComparison.Ordinal)).ToList());
            if (resolved != null)
                return resolved;
        }

        if (ctx.WildcardPackages.Count > 0)
        {
            resolved = preferFamily(candidates
                .Where(c => ctx.WildcardPackages.Any(p =>
                    c.FullName.StartsWith(p + ".", StringComparison.Ordinal))).ToList());
            if (resolved != null)
                return resolved;
        }

        return null;
    }

    static List<Symbol> collectCandidates(string name, ResolutionContext ctx)
    {
        if (ctx.ByName.Contains(name))
            return ctx.ByName[name].ToList();

        var withParens = $"{name}()";
        if (ctx.ByName.Contains(withParens))
            return ctx.ByName[withParens].ToList();

        var prefix = name + "(";
        var list = new List<Symbol>();
        foreach (var entry in ctx.ByName)
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                list.AddRange(entry);
        return list;
    }

    /// <summary>
    /// Unique candidate resolves; multiple candidates resolve to the first only
    /// when they are overloads of one declaring type (same FullName parent), a
    /// documented first-match tradeoff. Anything else is ambiguous → null so the
    /// edge is skipped instead of pointing at an arbitrary symbol.
    /// </summary>
    static Symbol? preferFamily(IReadOnlyList<Symbol> candidates)
    {
        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];

        var parents = candidates
            .Select(c =>
            {
                var dot = c.FullName.LastIndexOf('.');
                return dot > 0 ? c.FullName[..dot] : c.FullName;
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return parents.Count == 1 ? candidates[0] : null;
    }

    static string? extractTypeName(Node node)
        => node.Type switch
        {
            "type_identifier" or "identifier" => node.Text,
            "predefined_type" => node.Text,
            "scoped_type_identifier" => node.Text,
            "generic_type" => node.NamedChildren.FirstOrDefault()?.Text,
            "array_type" => node.NamedChildren is { Count: > 0 } aChild ? extractTypeName(aChild[0]) : null,
            _ => null,
        };

    static readonly HashSet<string> primitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
        "float", "double", "decimal", "bool", "char", "string", "object",
        "void", "var", "dynamic", "nint", "nuint",
        "number", "boolean", "symbol", "null", "undefined", "any", "never", "unknown",
        "String", "Integer", "Boolean", "Character", "Byte", "Short", "Long",
        "Float", "Double", "Void",
    };

    /// <summary>
    /// Builds the per-file resolution context. Java: reads the program-level
    /// package declaration and import declarations (probe-grounded: the dotted
    /// path is the named scoped_identifier child; wildcard imports add a named
    /// asterisk child; token fields have empty keys so the named children are
    /// the reliable read). Other languages: no package/import context.
    /// </summary>
    static ResolutionContext buildContext(ParseResult result, IReadOnlyList<Symbol> symbols, string filePath)
    {
        var byName = symbols.ToLookup(s => s.Name);
        var byFullName = symbols.ToLookup(s => s.FullName);

        string? package = null;
        Dictionary<string, string> imports = new();
        List<string> wildcards = new();

        if (result.Language == Parsing.Language.Java && result.TsTree is Tree tree)
        {
            foreach (var child in tree.RootNode.NamedChildren)
            {
                if (child.Type == "package_declaration")
                {
                    package = readPackage(child);
                }
                else if (child.Type == "import_declaration")
                {
                    var text = child.Text ?? "";
                    if (text.Contains(" static ", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (child.NamedChildren.Count == 0)
                        continue;

                    var dotted = child.NamedChildren[0].Text?.Trim();
                    if (string.IsNullOrWhiteSpace(dotted))
                        continue;

                    var isWildcard = child.NamedChildren.Count > 1
                        && child.NamedChildren[1].Type == "asterisk";
                    if (isWildcard)
                    {
                        wildcards.Add(dotted);
                    }
                    else
                    {
                        var dot = dotted.LastIndexOf('.');
                        var simple = dot > 0 ? dotted[(dot + 1)..] : dotted;
                        imports[simple] = dotted;
                    }
                }
            }
        }

        return new ResolutionContext(filePath, result.Language, package, imports, wildcards,
            symbols, byName, byFullName);
    }

    static string? readPackage(Node packageDecl)
    {
        var pkg = packageDecl.NamedChildren.LastOrDefault();
        if (pkg is not null && !string.IsNullOrWhiteSpace(pkg.Text))
            return pkg.Text.Trim();

        var raw = packageDecl.Text?.Trim();
        if (raw is null)
            return null;
        const string keyword = "package ";
        if (raw.StartsWith(keyword, StringComparison.Ordinal))
            raw = raw[keyword.Length..].Trim().TrimEnd(';').Trim();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    readonly ILogger<TreeSitterRelationshipExtractor> logger;

    public TreeSitterRelationshipExtractor(ILogger<TreeSitterRelationshipExtractor> logger)
        => this.logger = logger;

    void walkTree(Node node, ResolutionContext ctx,
        HashSet<string> seen, List<Relationship> results)
    {
        if (node == default) return;

        switch (node.Type)
        {
            case "class_declaration":
            case "abstract_class_declaration":
            case "interface_declaration":
            case "class_definition":
            case "type_declaration":
            case "trait_item":
            case "class_specifier":
                processHeritage(node, ctx, seen, results);
                break;
            case "call_expression":
            case "call":
                processCall(node, ctx, seen, results);
                break;
            case "method_invocation":
                processMethodInvocation(node, ctx, seen, results);
                break;
            case "new_expression":
            case "object_creation":
                processObjectCreation(node, ctx, seen, results);
                break;
        }

        checkTypeAnnotation(node, ctx, seen, results);

        foreach (var child in node.NamedChildren)
            walkTree(child, ctx, seen, results);
    }

    void processHeritageClause(Node clause, Symbol source, string relType,
        ResolutionContext ctx, HashSet<string> seen, List<Relationship> results)
    {
        foreach (var typeChild in clause.NamedChildren)
        {
            var typeName = extractTypeName(typeChild);
            if (typeName == null || primitiveTypes.Contains(typeName)) continue;
            var target = resolveSymbol(typeName, ctx);
            if (target == null || target.FullName == source.FullName) continue;
            addRelationship(source.FullName, target.FullName, relType, seen, results);
        }
    }

    void processHeritage(Node node, ResolutionContext ctx,
        HashSet<string> seen, List<Relationship> results)
    {
        var source = findContainingSymbol(node, ctx);
        if (source == null) return;

        foreach (var child in node.NamedChildren)
        {
            // TypeScript nests extends/implements under class_heritage
            if (child.Type == "class_heritage")
            {
                foreach (var sub in child.NamedChildren)
                    processNestedHeritageClause(sub, source, ctx, seen, results);
                continue;
            }

            if (child.Type is "heritage_clause" or "extends_clause" or "superclass")
                processHeritageClause(child, source, RelationshipTypes.Inherits, ctx, seen, results);

            if (child.Type == "implements_clause")
                processHeritageClause(child, source, RelationshipTypes.Implements, ctx, seen, results);
        }

        if (ctx.Language == Parsing.Language.Python)
        {
            var superField = node.Fields.FirstOrDefault(f => f.Key == "superclasses");
            if (superField.Key != null)
            {
                foreach (var child in superField.Value.NamedChildren)
                {
                    var typeName = extractTypeName(child);
                    if (typeName == null || primitiveTypes.Contains(typeName)) continue;
                    var target = resolveSymbol(typeName, ctx);
                    if (target == null || target.FullName == source.FullName) continue;
                    addRelationship(source.FullName, target.FullName, "Inherits", seen, results);
                }
            }
        }

        if (ctx.Language == Parsing.Language.Java)
        {
            var superField = node.Fields.FirstOrDefault(f => f.Key == "superclass");
            if (superField.Key != null)
            {
                var typeName = extractTypeName(superField.Value);

                if (typeName != null && !primitiveTypes.Contains(typeName))
                {
                    var target = resolveSymbol(typeName, ctx);

                    if (target != null && target.FullName != source.FullName)
                        addRelationship(source.FullName, target.FullName, "Inherits", seen, results);
                }
            }

            var interfacesField = node.Fields.FirstOrDefault(f => f.Key == "interfaces");
            if (interfacesField.Key != null)
                collectTypeRefs(interfacesField.Value, source, RelationshipTypes.Implements, ctx, seen, results);

            if (node.Type == "interface_declaration")
            {
                var extendsField = node.Fields.FirstOrDefault(f => f.Key == "extends");
                if (extendsField.Key != null)
                    collectTypeRefs(extendsField.Value, source, RelationshipTypes.Inherits, ctx, seen, results);
            }
        }

        // Go: embedded struct/interface fields (field_declaration without field_identifier)
        if (ctx.Language == Parsing.Language.Go && node.Type == "type_declaration")
        {
            foreach (var typeSpec in node.NamedChildren.Where(c => c.Type == "type_spec"))
            {
                foreach (var typeBody in typeSpec.NamedChildren.Where(c =>
                    c.Type is "struct_type" or "interface_type"))
                {
                    var fieldList = typeBody.NamedChildren
                        .FirstOrDefault(c => c.Type == "field_declaration_list");
                    if (fieldList == default) continue;

                    foreach (var field in fieldList.NamedChildren
                        .Where(c => c.Type == "field_declaration"))
                    {
                        if (field.NamedChildren.Any(c => c.Type == "field_identifier"))
                            continue;

                        var typeNode = field.NamedChildren.FirstOrDefault(c =>
                            c.Type is "type_identifier" or "scoped_type_identifier");
                        if (typeNode == default) continue;
                        var typeName = extractTypeName(typeNode);
                        if (typeName == null || primitiveTypes.Contains(typeName)) continue;
                        var target = resolveSymbol(typeName, ctx);
                        if (target == null || target.FullName == source.FullName) continue;
                        addRelationship(source.FullName, target.FullName, "Inherits", seen, results);
                    }
                }
            }
        }

        // Rust: supertrait bounds on trait_item
        if (ctx.Language == Parsing.Language.Rust && node.Type == "trait_item")
        {
            foreach (var child in node.NamedChildren)
            {
                if (child.Type == "trait_bounds")
                {
                    foreach (var bound in child.NamedChildren)
                    {
                        var typeName = extractTypeName(bound);
                        if (typeName == null || primitiveTypes.Contains(typeName)) continue;
                        var target = resolveSymbol(typeName, ctx);
                        if (target == null || target.FullName == source.FullName) continue;
                        addRelationship(source.FullName, target.FullName, "Inherits", seen, results);
                    }
                }
            }
        }

        // C++: extract base types from class_specifier text
        if (ctx.Language == Parsing.Language.Cpp && node.Type is "class_specifier")
        {
            foreach (var baseType in extractBaseTypes(node))
            {
                if (primitiveTypes.Contains(baseType)) continue;
                var target = resolveSymbol(baseType, ctx);
                if (target == null || target.FullName == source.FullName) continue;
                addRelationship(source.FullName, target.FullName, "Inherits", seen, results);
            }
        }
    }

    static List<string> extractBaseTypes(Node node)
    {
        // tree-sitter-cpp in TreeSitter.DotNet v1.3.0 does not expose inheritance
        // as structured nodes (no base_class_clause, base_specifier, etc.).
        // Fall back to text-based extraction from the class_specifier source.
        var text = node.Text.AsSpan();
        var nameStart = text.IndexOf("class", StringComparison.Ordinal) + 5;
        while (nameStart < text.Length && text[nameStart] == ' ') nameStart++;
        var nameEnd = nameStart;
        while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] != ':')
            nameEnd++;
        var colonIdx = text.Slice(nameEnd).IndexOf(':');
        if (colonIdx < 0) return [];
        var afterColon = text.Slice(nameEnd + colonIdx + 1);
        var braceIdx = afterColon.IndexOf('{');
        var baseSection = braceIdx >= 0 ? afterColon[..braceIdx].Trim() : afterColon.Trim();

        var result = new List<string>();
        foreach (var part in baseSection.ToString().Split(','))
        {
            var trimmed = part.Trim();
            // Remove access specifiers
            foreach (var keyword in new[] { "public ", "private ", "protected ", "virtual " })
                if (trimmed.StartsWith(keyword, StringComparison.Ordinal))
                    trimmed = trimmed[keyword.Length..].Trim();
            if (trimmed.Length > 0 && !result.Contains(trimmed))
                result.Add(trimmed);
        }
        return result;
    }

    void processNestedHeritageClause(Node clause, Symbol source,
        ResolutionContext ctx, HashSet<string> seen, List<Relationship> results)
    {
        if (clause.Type == "extends_clause")
            processHeritageClause(clause, source, RelationshipTypes.Inherits, ctx, seen, results);
        else if (clause.Type == "implements_clause")
            processHeritageClause(clause, source, RelationshipTypes.Implements, ctx, seen, results);
        else
            processHeritageClause(clause, source, RelationshipTypes.Inherits, ctx, seen, results);
    }

    void collectTypeRefs(Node typeListNode, Symbol source, string relType,
        ResolutionContext ctx, HashSet<string> seen, List<Relationship> results)
    {
        if (typeListNode.Type == "type_list")
        {
            foreach (var child in typeListNode.NamedChildren)
            {
                var typeName = extractTypeName(child);
                if (typeName == null || primitiveTypes.Contains(typeName)) continue;

                var target = resolveSymbol(typeName, ctx);
                if (target == null || target.FullName == source.FullName) continue;

                addRelationship(source.FullName, target.FullName, relType, seen, results);
            }
        }
        else
        {
            var typeName = extractTypeName(typeListNode);
            if (typeName != null && !primitiveTypes.Contains(typeName))
            {
                var target = resolveSymbol(typeName, ctx);
                if (target != null && target.FullName != source.FullName)
                    addRelationship(source.FullName, target.FullName, relType, seen, results);
            }
        }
    }

    void processCall(Node node, ResolutionContext ctx,
        HashSet<string> seen, List<Relationship> results)
    {
        var source = findContainingSymbol(node, ctx);
        if (source == null) return;

        var funcField = node.Fields.FirstOrDefault(f => f.Key == "function");
        if (funcField.Key == null) return;

        var funcNode = funcField.Value;
        string? methodName = null;

        if (funcNode.Type is "identifier" or "property_identifier")
            methodName = funcNode.Text;
        else if (funcNode.Type == "member_expression")
        {
            var propField = funcNode.Fields.FirstOrDefault(f => f.Key == "property");
            if (propField.Key != null)
                methodName = propField.Value.Text;
        }

        if (methodName == null || primitiveTypes.Contains(methodName))
            return;

        var target = resolveSymbol(methodName, ctx);
        if (target == null || target.FullName == source.FullName)
            return;

        addRelationship(source.FullName, target.FullName, RelationshipTypes.Calls, seen, results);
    }

    void processMethodInvocation(Node node, ResolutionContext ctx,
        HashSet<string> seen, List<Relationship> results)
    {
        var source = findContainingSymbol(node, ctx);
        if (source == null) return;

        var nameField = node.Fields.FirstOrDefault(f => f.Key == "name");
        if (nameField.Key == null) return;

        var methodName = nameField.Value.Text;
        if (primitiveTypes.Contains(methodName))
            return;

        var target = resolveSymbol(methodName, ctx);
        if (target == null || target.FullName == source.FullName)
            return;

        addRelationship(source.FullName, target.FullName, RelationshipTypes.Calls, seen, results);
    }

    void processObjectCreation(Node node, ResolutionContext ctx,
        HashSet<string> seen, List<Relationship> results)
    {
        var source = findContainingSymbol(node, ctx);
        if (source == null) return;

        var typeField = node.Fields.FirstOrDefault(f => f.Key is "constructor" or "type");
        if (typeField.Key == null) return;

        var typeName = extractTypeName(typeField.Value);
        if (typeName == null || primitiveTypes.Contains(typeName))
            return;

        var target = resolveSymbol(typeName, ctx);
        if (target == null || target.FullName == source.FullName)
            return;

        addRelationship(source.FullName, target.FullName, RelationshipTypes.References, seen, results);
    }

    void checkTypeAnnotation(Node node, ResolutionContext ctx,
        HashSet<string> seen, List<Relationship> results)
    {
        var typeField = node.Fields.FirstOrDefault(f => f.Key == "type");
        if (typeField.Key == null) return;

        var source = findContainingSymbol(node, ctx);
        if (source == null) return;

        string? typeName;
        if (typeField.Value.Type == "type_annotation")
        {
            var typeExpr = typeField.Value.NamedChildren.FirstOrDefault();
            if (typeExpr == default) return;
            typeName = extractTypeName(typeExpr);
        }
        else
            typeName = extractTypeName(typeField.Value);

        if (typeName == null || primitiveTypes.Contains(typeName))
            return;

        var target = resolveSymbol(typeName, ctx);
        if (target == null || target.FullName == source.FullName)
            return;

        addRelationship(source.FullName, target.FullName, RelationshipTypes.References, seen, results);
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

        var ctx = buildContext(result, symbols, filePath);
        var results = new List<Relationship>();
        var seen = new HashSet<string>();

        walkTree(tree.RootNode, ctx, seen, results);

        logger.LogDebug("Extracted {Count} relationships from {File}", results.Count, filePath);
        return results;
    }
}