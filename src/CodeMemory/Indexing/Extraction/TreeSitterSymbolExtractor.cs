using CodeMemory.Indexing.Parsing;
using Microsoft.Extensions.Logging;
using TreeSitter;

namespace CodeMemory.Indexing.Extraction;

public sealed class TreeSitterSymbolExtractor : ISymbolExtractor
{
    readonly record struct LanguageConfig(
        TreeSitter.Language Language,
        string Query,
        Dictionary<string, CodeSymbolKind> KindMap);

    static string? buildFullName(Node node, CodeSymbolKind kind, LanguageConfig config, string name)
    {
        var parts = new List<string>();
        var current = node.Parent;

        while (current != default && current.Type != "program")
        {
            if (classLikeTypes.Contains(current.Type) || config.KindMap.ContainsKey(current.Type))
            {
                var parentName = getNodeName(current);

                if (parentName != null)
                    parts.Insert(0, parentName);
            }
            current = current.Parent;
        }

        if (name is not null && (methodLikeTypes.Contains(node.Type) || node.Type == "constructor_declaration"))
            name = buildMethodName(node, name);

        if (parts.Count > 0)
            return $"{string.Join(".", parts)}.{name}";

        return name;
    }

    static string buildMethodName(Node node, string baseName)
    {
        var paramNames = new List<string>();
        var paramsField = node.Fields.FirstOrDefault(f => f.Key == "parameters");

        if (paramsField.Key != null)
            foreach (var param in paramsField.Value.NamedChildren)
            {
                var typeField = param.Fields.FirstOrDefault(f => f.Key == "type");
                var typeName = param.Type == "identifier" ? "" : "var";

                if (typeField.Key != null)
                {
                    var typeNode = typeField.Value;
                    typeName = typeNode.Text;
                }

                var nameField = param.Fields.FirstOrDefault(f => f.Key == "name");
                var paramName = nameField.Key != null ? nameField.Value.Text
                    : param.Type == "identifier" ? param.Text
                    : "";

                if (string.IsNullOrEmpty(typeName))
                    paramNames.Add(paramName);
                else
                    paramNames.Add($"{typeName} {paramName}");
            }

        return $"{baseName}({string.Join(", ", paramNames)})";
    }

    static string? getNodeName(Node node)
    {
        var nameField = node.Fields.FirstOrDefault(f => f.Key == "name");

        if (nameField.Key != null)
            return nameField.Value.Text;

        return null;
    }

    static bool isTopLevelVariable(Node node)
    {
        if (node.Type != "variable_declarator")
            return false;

        var parent = node.Parent;
        if (parent == default)
            return false;

        if (!variableDeclTypes.Contains(parent.Type))
            return false;

        var grandparent = parent.Parent;
        if (grandparent == default)
            return false;

        if (grandparent.Type == "program")
            return true;

        if (grandparent.Type == "export_statement")
            return grandparent.Parent?.Type == "program";

        return false;
    }

    static CodeSymbolKind? getKind(string nodeType, Dictionary<string, CodeSymbolKind> kindMap)
    {
        if (kindMap.TryGetValue(nodeType, out var kind))
            return kind;

        if (nodeType == "variable_declarator")
            return CodeSymbolKind.Variable;

        return null;
    }

    static IReadOnlyList<string> extractModifiers(Node node, string fileText)
    {
        var modifiers = new List<string>();

        // Check for export (parent is export_statement)
        if (node?.Parent?.Type == "export_statement" ||
            (node?.Parent?.Parent != default && node.Parent.Parent.Type == "export_statement"))

            modifiers.Add("export");

        // Check children for modifier keywords
        foreach (var child in node?.Children ?? [])
            if (!child.IsNamed)
            {
                var modifierText = child.Type;

                if (modifierText is "abstract" or "static" or "async" or "readonly"
                    or "public" or "private" or "protected" or "sealed" or "override"
                    or "virtual" or "const" or "default" or "extern")

                    if (!modifiers.Contains(modifierText))
                        modifiers.Add(modifierText);
            }

        // Java/C/C++: check modifier container child nodes
        foreach (var child in node?.NamedChildren ?? [])
        {
            if (child.Type is "modifiers" or "storage_class_specifier" or "type_qualifier")
                foreach (var mod in child.Children)
                    if (!mod.IsNamed)
                    {
                        var modText = mod.Type;

                        if (!modifiers.Contains(modText))
                            modifiers.Add(modText);
                    }
        }

        return modifiers;
    }

    static string? extractDocumentation(Node node, string fileText)
    {
        var prev = node.PreviousNamedSibling;

        if (prev != default && prev.Type == "comment")
        {
            var text = prev.Text;

            if (text.StartsWith("/**") || text.StartsWith("///") || text.StartsWith("/*"))
            {
                var doc = text.TrimStart('/', '*', ' ', '\t').TrimEnd('*', '/', ' ', '\t');
                return string.IsNullOrEmpty(doc) ? null : doc;
            }
        }

        return null;
    }

    static LanguageConfig? getLanguageConfig(Parsing.Language language)
        => language switch
        {
            Parsing.Language.TypeScript => new LanguageConfig(tsLanguage, tsQuery, tsKindMap),
            Parsing.Language.JavaScript => new LanguageConfig(jsLanguage, jsQuery, jsKindMap),
            Parsing.Language.Java => new LanguageConfig(javaLanguage, javaQuery, javaKindMap),
            Parsing.Language.Python => new LanguageConfig(pythonLanguage, pythonQuery, pythonKindMap),
            Parsing.Language.Go => new LanguageConfig(goLanguage, goQuery, goKindMap),
            Parsing.Language.Rust => new LanguageConfig(rustLanguage, rustQuery, rustKindMap),
            Parsing.Language.C => new LanguageConfig(cLanguage, cQuery, cKindMap),
            Parsing.Language.Cpp => new LanguageConfig(cppLanguage, cppQuery, cppKindMap),
            Parsing.Language.HTML => null,
            _ => null,
        };

    static readonly TreeSitter.Language tsLanguage = new("TypeScript");
    static readonly TreeSitter.Language jsLanguage = new("JavaScript");
    static readonly TreeSitter.Language javaLanguage = new("Java");
    static readonly TreeSitter.Language pythonLanguage = new("Python");
    static readonly TreeSitter.Language goLanguage = new("Go");
    static readonly TreeSitter.Language rustLanguage = new("Rust");
    static readonly TreeSitter.Language cLanguage = new("C");
    static readonly TreeSitter.Language cppLanguage = new("Cpp");

    static readonly string tsQuery = string.Join(" ",
        "(class_declaration name: (_) @name) @node",
        "(abstract_class_declaration name: (_) @name) @node",
        "(interface_declaration name: (_) @name) @node",
        "(enum_declaration name: (_) @name) @node",
        "(function_declaration name: (_) @name) @node",
        "(method_definition name: (_) @name) @node",
        "(method_signature name: (_) @name) @node",
        "(abstract_method_signature name: (_) @name) @node",
        "(public_field_definition name: (_) @name) @node",
        "(property_signature name: (_) @name) @node",
        "(type_alias_declaration name: (_) @name) @node",
        "(module name: (_) @name) @node",
        "(lexical_declaration (variable_declarator name: (_) @name) @node)",
        "(variable_declaration (variable_declarator name: (_) @name) @node)"
    );

    static readonly string jsQuery = string.Join(" ",
        "(class_declaration name: (_) @name) @node",
        "(function_declaration name: (_) @name) @node",
        "(method_definition name: (_) @name) @node",
        "(lexical_declaration (variable_declarator name: (_) @name) @node)",
        "(variable_declaration (variable_declarator name: (_) @name) @node)"
    );

    static readonly string javaQuery = string.Join(" ",
        "(class_declaration name: (_) @name) @node",
        "(interface_declaration name: (_) @name) @node",
        "(enum_declaration name: (_) @name) @node",
        "(record_declaration name: (_) @name) @node",
        "(method_declaration name: (_) @name) @node",
        "(constructor_declaration name: (_) @name) @node",
        "(field_declaration (variable_declarator name: (_) @name)) @node",
        "(annotation_type_declaration name: (_) @name) @node"
    );

    static readonly string pythonQuery = string.Join(" ",
        "(class_definition name: (_) @name) @node",
        "(function_definition name: (_) @name) @node"
    );
    static readonly string goQuery = string.Join(" ",
        "(type_spec name: (_) @name) @node",
        "(function_declaration name: (_) @name) @node",
        "(method_declaration name: (_) @name) @node"
    );

    static readonly string rustQuery = string.Join(" ",
        "(struct_item name: (_) @name) @node",
        "(enum_item name: (_) @name) @node",
        "(trait_item name: (_) @name) @node",
        "(function_item name: (_) @name) @node"
    );

    static readonly string cQuery = string.Join(" ",
        "(struct_specifier name: (_) @name) @node",
        "(function_definition declarator: (function_declarator declarator: (_) @name)) @node",
        "(declaration declarator: (function_declarator declarator: (_) @name)) @node",
        "(enum_specifier name: (_) @name) @node",
        "(union_specifier name: (_) @name) @node",
        "(declaration declarator: (identifier) @name) @node",
        "(declaration declarator: (init_declarator declarator: (identifier) @name)) @node",
        "(declaration declarator: (pointer_declarator declarator: (identifier) @name)) @node",
        "(declaration declarator: (array_declarator declarator: (identifier) @name)) @node"
    );

    static readonly string cppQuery = string.Join(" ",
        "(class_specifier name: (_) @name) @node",
        "(struct_specifier name: (_) @name) @node",
        "(enum_specifier name: (_) @name) @node",
        "(union_specifier name: (_) @name) @node",
        "(function_definition declarator: (function_declarator declarator: (_) @name)) @node",
        "(declaration declarator: (function_declarator declarator: (_) @name)) @node",
        "(namespace_definition name: (_) @name) @node",
        "(type_definition declarator: (_) @name) @node",
        "(alias_declaration name: (_) @name) @node",
        "(declaration declarator: (identifier) @name) @node",
        "(declaration declarator: (init_declarator declarator: (identifier) @name)) @node",
        "(declaration declarator: (pointer_declarator declarator: (identifier) @name)) @node",
        "(declaration declarator: (array_declarator declarator: (identifier) @name)) @node"
    );

    static readonly HashSet<string> methodLikeTypes = new(StringComparer.Ordinal)
    {
        "method_definition", "method_signature", "abstract_method_signature",
        "function_declaration", "method_declaration", "constructor_declaration",
        "function_definition", "function_item",
    };

    static readonly HashSet<string> classLikeTypes = new(StringComparer.Ordinal)
    {
        "class_declaration", "abstract_class_declaration",
        "interface_declaration", "enum_declaration",
        "record_declaration", "annotation_type_declaration",
        "struct_declaration",
        "class_definition", "struct_item", "trait_item", "enum_item", "impl_item",
        "class_specifier", "struct_specifier", "enum_specifier",
        "namespace_definition",
    };

    static readonly HashSet<string> variableDeclTypes = new(StringComparer.Ordinal)
    {
        "lexical_declaration", "variable_declaration",
    };

    static readonly Dictionary<string, CodeSymbolKind> tsKindMap = new(StringComparer.Ordinal)
    {
        ["class_declaration"] = CodeSymbolKind.Class,
        ["abstract_class_declaration"] = CodeSymbolKind.Class,
        ["interface_declaration"] = CodeSymbolKind.Interface,
        ["enum_declaration"] = CodeSymbolKind.Enum,
        ["function_declaration"] = CodeSymbolKind.Function,
        ["method_definition"] = CodeSymbolKind.Method,
        ["method_signature"] = CodeSymbolKind.Method,
        ["abstract_method_signature"] = CodeSymbolKind.Method,
        ["public_field_definition"] = CodeSymbolKind.Property,
        ["property_signature"] = CodeSymbolKind.Property,
        ["type_alias_declaration"] = CodeSymbolKind.TypeAlias,
        ["module"] = CodeSymbolKind.Module,
    };

    static readonly Dictionary<string, CodeSymbolKind> jsKindMap = new(StringComparer.Ordinal)
    {
        ["class_declaration"] = CodeSymbolKind.Class,
        ["function_declaration"] = CodeSymbolKind.Function,
        ["method_definition"] = CodeSymbolKind.Method,
    };

    static readonly Dictionary<string, CodeSymbolKind> javaKindMap = new(StringComparer.Ordinal)
    {
        ["class_declaration"] = CodeSymbolKind.Class,
        ["interface_declaration"] = CodeSymbolKind.Interface,
        ["enum_declaration"] = CodeSymbolKind.Enum,
        ["record_declaration"] = CodeSymbolKind.Record,
        ["method_declaration"] = CodeSymbolKind.Method,
        ["constructor_declaration"] = CodeSymbolKind.Constructor,
        ["field_declaration"] = CodeSymbolKind.Field,
        ["annotation_type_declaration"] = CodeSymbolKind.Annotation,
    };

    static readonly Dictionary<string, CodeSymbolKind> pythonKindMap = new(StringComparer.Ordinal)
    {
        ["class_definition"] = CodeSymbolKind.Class,
        ["function_definition"] = CodeSymbolKind.Function,
    };

    static readonly Dictionary<string, CodeSymbolKind> goKindMap = new(StringComparer.Ordinal)
    {
        ["type_spec"] = CodeSymbolKind.Class,
        ["function_declaration"] = CodeSymbolKind.Function,
        ["method_declaration"] = CodeSymbolKind.Method,
    };

    static readonly Dictionary<string, CodeSymbolKind> rustKindMap = new(StringComparer.Ordinal)
    {
        ["struct_item"] = CodeSymbolKind.Struct,
        ["enum_item"] = CodeSymbolKind.Enum,
        ["trait_item"] = CodeSymbolKind.Interface,
        ["function_item"] = CodeSymbolKind.Function,
    };

    static readonly Dictionary<string, CodeSymbolKind> cKindMap = new(StringComparer.Ordinal)
    {
        ["struct_specifier"] = CodeSymbolKind.Struct,
        ["function_definition"] = CodeSymbolKind.Function,
        ["declaration"] = CodeSymbolKind.Function,
        ["enum_specifier"] = CodeSymbolKind.Enum,
        ["union_specifier"] = CodeSymbolKind.Struct,
    };

    static readonly Dictionary<string, CodeSymbolKind> cppKindMap = new(StringComparer.Ordinal)
    {
        ["class_specifier"] = CodeSymbolKind.Class,
        ["struct_specifier"] = CodeSymbolKind.Struct,
        ["enum_specifier"] = CodeSymbolKind.Enum,
        ["union_specifier"] = CodeSymbolKind.Struct,
        ["function_definition"] = CodeSymbolKind.Function,
        ["declaration"] = CodeSymbolKind.Function,
        ["namespace_definition"] = CodeSymbolKind.Module,
        ["type_definition"] = CodeSymbolKind.TypeAlias,
        ["alias_declaration"] = CodeSymbolKind.TypeAlias,
        ["template_declaration"] = CodeSymbolKind.Class,
    };

    static bool isInsideClass(Node node)
    {
        var current = node.Parent;
        while (current != default)
        {
            if (current.Type is "class_definition" or "impl_item"
                or "class_specifier" or "struct_specifier")
                return true;
            if (current.Type is "program" or "module" or "translation_unit")
                return false;
            current = current.Parent;
        }
        return false;
    }

    readonly ILogger<TreeSitterSymbolExtractor> logger;

    public TreeSitterSymbolExtractor(ILogger<TreeSitterSymbolExtractor> logger)
    {
        this.logger = logger;
        this.logger.LogDebug("TreeSitterSymbolExtractor created");
    }

    void processMatch(
        QueryMatch match,
        LanguageConfig config,
        string fileText,
        string filePath,
        List<Symbol> symbols,
        HashSet<string> seenFullNames)
    {
        var nodeCap = match.Captures.FirstOrDefault(c => c.Name == "node");
        var nameCap = match.Captures.FirstOrDefault(c => c.Name == "name");

        if (nodeCap?.Node == default || nameCap?.Node == default)
            return;

        var node = nodeCap.Node;
        var nameNode = nameCap.Node;
        var nodeType = node.Type;

        // Handle variable declarations: only include top-level ones
        if (variableDeclTypes.Contains(nodeType))
            nodeType = "variable_declarator";

        if (nodeType == "variable_declarator" && !isTopLevelVariable(node))
            return;

        // C/C++: distinguish variable declarations from function prototypes
        if (nodeType == "declaration")
        {
            var declField = node.Fields.FirstOrDefault(f => f.Key == "declarator");
            if (declField.Key != null && declField.Value.Type != "function_declarator")
            {
                if (node.Parent?.Type == "translation_unit")
                    nodeType = "variable_declarator";
                else
                    return;
            }
        }

        var kind = getKind(nodeType, config.KindMap);
        if (kind == null)
            return;

        // Python: reclassify function_definition inside class_definition as Method
        if (nodeType == "function_definition" && isInsideClass(node))
            kind = CodeSymbolKind.Method;

        // Rust: reclassify function_item inside impl_item as Method
        if (nodeType == "function_item" && isInsideClass(node))
            kind = CodeSymbolKind.Method;

        var name = nameNode.Text;
        var lineRange = new LineRange(
            node.StartPosition.Row + 1,
            node.EndPosition.Row + 1);

        var fullName = buildFullName(node, kind.Value, config, name);
        var modifiers = extractModifiers(node, fileText);
        var documentation = extractDocumentation(node, fileText);

        if (fullName is null || !seenFullNames.Add(fullName))
            return;

        symbols.Add(new Symbol(
            name,
            kind.Value,
            filePath,
            lineRange,
            fullName,
            modifiers,
            documentation));
    }

    public IReadOnlyList<Symbol> Extract(ParseResult result, string filePath)
    {
        if (result.TsTree is not Tree tree)
            return Array.Empty<Symbol>();

        var root = tree.RootNode;
        var config = getLanguageConfig(result.Language);

        if (config == null)
            return Array.Empty<Symbol>();

        var symbols = new List<Symbol>();
        var seenFullNames = new HashSet<string>();

        try
        {
            using var query = new Query(config.Value.Language, config.Value.Query);
            using var cursor = query.Execute(root);

            foreach (var match in cursor.Matches)
                processMatch(match, config.Value, result.FileText, filePath, symbols, seenFullNames);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tree-sitter query failed for: {FilePath}", filePath);
        }

        logger.LogDebug("Extracted {Count} symbols from {File}", symbols.Count, filePath);

        return symbols;
    }
}
