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
    /// fully-qualified name → explicit import → typed-receiver members → same file →
    /// same package → wildcard-imported package → unique name overall → null
    /// (edge skipped).
    ///
    /// <paramref name="typesOnly"/> narrows the by-name candidate pool to type kinds
    /// (class/interface/struct/enum/record/type alias) so constructors, methods and
    /// fields that share a type's name never compete when resolving a type reference.
    /// <paramref name="receiverTypeFullName"/> is the resolved declaring type of a
    /// member-access receiver (obj.method()): members of that type are preferred as
    /// stronger evidence than file/package proximity, and zero matches means the
    /// member is not in the index (e.g. implicit enum methods) — the edge is skipped
    /// rather than guessed.
    ///
    /// Method overloads of a single declaring type resolve to the first match —
    /// the same documented tradeoff as the storage signature-prefix step.
    /// </summary>
    static Symbol? resolveSymbol(string name, ResolutionContext ctx,
        bool typesOnly = false, string? receiverTypeFullName = null)
    {
        if (primitiveTypes.Contains(name))
            return null;

        // Fully-qualified reference text (contains dots) → exact FullName match.
        // Bare identifiers never take this path: a top-level symbol whose FullName
        // equals the bare name (e.g. a TypeScript module-level `name`) must not win
        // over same-language candidates (#131 Change 4).
        if (name.Contains('.') && ctx.ByFullName.Contains(name))
            return ctx.ByFullName[name].First();

        if (ctx.Imports.TryGetValue(name, out var qualified) && ctx.ByFullName.Contains(qualified))
            return ctx.ByFullName[qualified].First();

        var candidates = collectCandidates(name, ctx, typesOnly);
        if (candidates.Count == 0)
            return null;

        // Typed receiver: obj.method() → prefer members of obj's declared type.
        // Stronger evidence than file/package proximity; zero matches means the
        // member is not in the index (implicit enum methods, external types).
        if (receiverTypeFullName != null)
            return preferFamily(candidates
                .Where(c => c.FullName.StartsWith(
                    receiverTypeFullName + ".", StringComparison.Ordinal))
                .ToList());

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

    static List<Symbol> collectCandidates(string name, ResolutionContext ctx, bool typesOnly)
    {
        List<Symbol> result;
        if (ctx.ByName.Contains(name))
            result = ctx.ByName[name].ToList();
        else
        {
            var withParens = $"{name}()";
            if (ctx.ByName.Contains(withParens))
                result = ctx.ByName[withParens].ToList();
            else
            {
                var prefix = name + "(";
                result = new List<Symbol>();
                foreach (var entry in ctx.ByName)
                    if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                        result.AddRange(entry);
            }
        }

        IEnumerable<Symbol> filtered = result;
        if (typesOnly)
            filtered = filtered.Where(s => s.Kind is CodeSymbolKind.Class or CodeSymbolKind.Interface
                or CodeSymbolKind.Struct or CodeSymbolKind.Enum or CodeSymbolKind.Record
                or CodeSymbolKind.TypeAlias);

        // Cross-language references are meaningless by construction — a Java file can
        // never reference a TypeScript member. Unknown-language symbols (the C# /
        // Roslyn path owns its own cross-references) are excluded from tree-sitter
        // resolution pools entirely.
        return filtered.Where(s => s.Language == ctx.Language).ToList();
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
    /// Resolves the declared type (as a symbol FullName) of a member-access
    /// receiver using declarations visible in the current file: parameters, locals
    /// and fields. Returns null for `this`/`super` receivers, unnamed receivers,
    /// primitive types, and type names that cannot be resolved — the caller then
    /// falls back to the general resolution pipeline.
    /// </summary>
    static string? resolveReceiverType(Node receiver, ResolutionContext ctx)
    {
        if (receiver == default)
            return null;

        switch (receiver.Type)
        {
            case "identifier":
            case "attribute_identifier":
            {
                var receiverName = receiver.Text;
                if (string.IsNullOrWhiteSpace(receiverName))
                    return null;

                var typeName = findDeclaredTypeName(receiver, receiverName);
                if (typeName is null || primitiveTypes.Contains(typeName))
                    return null;

                var typeSymbol = resolveSymbol(typeName, ctx, typesOnly: true);
                return typeSymbol?.FullName;
            }
            case "member_expression":
            case "field_access":
            {
                // a.b.method() — recurse into the innermost object.
                var objField = receiver.Fields.FirstOrDefault(f => f.Key == "object");
                return objField.Key != null ? resolveReceiverType(objField.Value, ctx) : null;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Searches declarations visible at the receiver site — the nearest enclosing
    /// method/constructor scope first, then the class body's fields — for the
    /// receiver identifier and returns its declared type text.
    /// </summary>
    static string? findDeclaredTypeName(Node receiver, string name)
    {
        var scope = receiver.Parent;
        Node? methodScope = null;
        Node? classScope = null;

        while (scope != default)
        {
            if (methodScope == null && scope.Type is "method_declaration" or "constructor_declaration"
                or "method_definition" or "function_declaration" or "function_definition"
                or "arrow_function" or "arrow_function_expression")
                methodScope = scope;
            if (classScope == null && scope.Type is "class_declaration" or "class_definition"
                or "class_specifier" or "struct_specifier" or "interface_declaration"
                or "interface_type_declaration")
                classScope = scope;
            if (methodScope != null && classScope != null)
                break;
            scope = scope.Parent;
        }

        if (methodScope != default)
        {
            var budget = 400;
            var typeName = searchScope(methodScope, name, ref budget);
            if (typeName != null)
                return typeName;
        }

        if (classScope != default)
        {
            // Fields only — direct members of the class body, never the internals
            // of sibling methods (a parameter in another method must not satisfy a
            // receiver lookup here).
            var body = classScope.NamedChildren.FirstOrDefault(c => c.Type == "class_body");
            var members = body == default ? classScope.NamedChildren : body.NamedChildren;
            foreach (var member in members)
            {
                var typeName = typeOfDeclarationFor(member, name);
                if (typeName != null)
                    return typeName;
            }
        }

        return null;
    }

    /// <summary>
    /// Bounded descendant search for a declaration of <paramref name="name"/>,
    /// returning its declared type text. <paramref name="budget"/> caps visited
    /// nodes so receiver lookups stay linear in practice.
    /// </summary>
    static string? searchScope(Node node, string name, ref int budget)
    {
        if (budget <= 0)
            return null;
        budget--;

        var typeName = typeOfDeclarationFor(node, name);
        if (typeName != null)
            return typeName;

        foreach (var child in node.NamedChildren)
        {
            var result = searchScope(child, name, ref budget);
            if (result != null)
                return result;
        }
        return null;
    }

    /// <summary>
    /// Returns the declared type text if <paramref name="node"/> is a declaration
    /// (parameter, class field, local or variable declarator) that declares
    /// <paramref name="name"/>; null otherwise.
    /// </summary>
    static string? typeOfDeclarationFor(Node node, string name)
    {
        switch (node.Type)
        {
            case "formal_parameter":
            case "parameter":
            case "required_parameter":
            case "optional_parameter":
            case "field_definition":
            case "property_signature":
            {
                var nameField = node.Fields.FirstOrDefault(f => f.Key == "name");
                if (nameField.Key == null || nameField.Value.Text != name)
                    return null;
                var typeField = node.Fields.FirstOrDefault(f => f.Key == "type");
                return typeField.Key != null ? declaredTypeText(typeField.Value) : null;
            }
            case "variable_declarator":
            {
                var nameField = node.Fields.FirstOrDefault(f => f.Key == "name");
                if (nameField.Key == null || nameField.Value.Text != name)
                    return null;
                var typeField = node.Fields.FirstOrDefault(f => f.Key == "type");
                return typeField.Key != null ? declaredTypeText(typeField.Value) : null;
            }
            case "field_declaration":
            case "local_variable_declaration":
            case "variable_declaration":
            {
                var typeField = node.Fields.FirstOrDefault(f => f.Key == "type");
                if (typeField.Key == null)
                    return null;
                foreach (var field in node.Fields)
                {
                    if (field.Key != "declarator")
                        continue;
                    if (variableDeclaratorName(field.Value) == name)
                        return declaredTypeText(typeField.Value);
                }
                return null;
            }
            default:
                return null;
        }
    }

    static string? variableDeclaratorName(Node declarator)
    {
        var nameField = declarator.Fields.FirstOrDefault(f => f.Key == "name");
        if (nameField.Key != null)
            return nameField.Value.Text;
        return declarator.NamedChildren.FirstOrDefault(c => c.Type == "identifier")?.Text;
    }

    static string? declaredTypeText(Node typeNode)
    {
        if (typeNode == default)
            return null;
        var effective = typeNode.Type == "type_annotation"
            ? typeNode.NamedChildren.FirstOrDefault()
            : typeNode;
        return effective == default ? null : extractTypeName(effective);
    }

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
            var target = resolveSymbol(typeName, ctx, typesOnly: true);
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
                    var target = resolveSymbol(typeName, ctx, typesOnly: true);
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
                    var target = resolveSymbol(typeName, ctx, typesOnly: true);

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
                        var target = resolveSymbol(typeName, ctx, typesOnly: true);
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
                        var target = resolveSymbol(typeName, ctx, typesOnly: true);
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
                var target = resolveSymbol(baseType, ctx, typesOnly: true);
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

                var target = resolveSymbol(typeName, ctx, typesOnly: true);
                if (target == null || target.FullName == source.FullName) continue;

                addRelationship(source.FullName, target.FullName, relType, seen, results);
            }
        }
        else
        {
            var typeName = extractTypeName(typeListNode);
            if (typeName != null && !primitiveTypes.Contains(typeName))
            {
                var target = resolveSymbol(typeName, ctx, typesOnly: true);
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
        Node? receiverNode = default;

        if (funcNode.Type is "identifier" or "property_identifier")
            methodName = funcNode.Text;
        else if (funcNode.Type == "member_expression")
        {
            var propField = funcNode.Fields.FirstOrDefault(f => f.Key == "property");
            if (propField.Key != null)
                methodName = propField.Value.Text;
            var objField = funcNode.Fields.FirstOrDefault(f => f.Key == "object");
            if (objField.Key != null)
                receiverNode = objField.Value;
        }

        if (methodName == null || primitiveTypes.Contains(methodName))
            return;

        var receiverType = receiverNode == default ? null : resolveReceiverType(receiverNode, ctx);
        var target = resolveSymbol(methodName, ctx, receiverTypeFullName: receiverType);
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

        var objField = node.Fields.FirstOrDefault(f => f.Key == "object");
        var receiverType = objField.Key == null ? null : resolveReceiverType(objField.Value, ctx);
        var target = resolveSymbol(methodName, ctx, receiverTypeFullName: receiverType);
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

        var target = resolveSymbol(typeName, ctx, typesOnly: true);
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

        var target = resolveSymbol(typeName, ctx, typesOnly: true);
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