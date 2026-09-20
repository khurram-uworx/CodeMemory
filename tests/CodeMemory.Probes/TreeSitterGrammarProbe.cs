namespace CodeMemory.Probes;

/// <summary>
/// Probe: what do the bundled tree-sitter grammars actually emit for the shapes that
/// <see cref="CodeMemory.Indexing.Extraction.TreeSitterSymbolExtractor"/> indexes —
/// Java package/class nesting and TypeScript namespace (internal_module) vs module?
///
/// Prints the named-node tree with field values plus the qualification chain the
/// extractor's buildFullName sees, so grammar-version changes or new-language
/// additions can be investigated without guessing.
/// </summary>
internal static class TreeSitterGrammarProbe
{
    public static int Run()
    {
        Console.WriteLine("=== Tree-sitter grammar shapes ===");

        Dump("Java: package + class + method", "Java", """
            package uk.co.uworx.khoji.agile.internal.error;

            public class ServiceException {
                public void doSomething(String arg) {}
            }
            """);

        Dump("Java: default package", "Java", """
            public class NoPackageClass {
                public void doSomething() {}
            }
            """);

        Dump("Java: package + nested class", "Java", """
            package uk.co.uworx.khoji.agile.internal.model;

            public class Outer {
                class Inner {
                    void run() {}
                }
            }
            """);

        Dump("TypeScript: namespace + exported class", "TypeScript", """
            namespace MyNs {
                export class Worker {
                    run() {}
                }
            }
            """);

        Dump("TypeScript: module + class", "TypeScript", """
            module Legacy {
                class OldWorker {}
            }
            """);

        Dump("C++: namespace + class + method", "Cpp", """
            namespace MyNs {
                class Worker {
                    int run() { return 0; }
                };
            }
            """);

        return 0;
    }

    static void Dump(string title, string languageName, string code)
    {
        Console.WriteLine();
        Console.WriteLine($"===== {title} =====");
        try
        {
            using var parser = new TreeSitter.Parser(new TreeSitter.Language(languageName));
            using var tree = parser.Parse(code);
            if (tree is null)
            {
                Console.WriteLine("  (null tree)");
                return;
            }

            var root = tree.RootNode;

            Console.WriteLine("-- node tree (named nodes with field values) --");
            DumpNode(root);

            var firstType = FindFirst(root,
                n => n.Type is "class_declaration" or "record_declaration"
                    or "interface_declaration" or "enum_declaration");
            if (firstType is null)
                return;

            Console.WriteLine($"-- qualification chain for {firstType.Type} '{firstType.Text}' --");
            var ancestors = new List<string>();
            var current = firstType.Parent;
            while (current is not null && current.Type != "program")
            {
                var name = NodeName(current);
                ancestors.Add(name is null ? current.Type : $"{current.Type} (name='{name}')");
                current = current.Parent;
            }
            Console.WriteLine("  " + (ancestors.Count > 0 ? string.Join(" <- ", ancestors) : "<none>"));
            Console.WriteLine("  program-level package: " + (ProgramPackage(root) ?? "<none>"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Mirrors TreeSitterSymbolExtractor.getNodeName: the text of the node's "name" field.
    /// </summary>
    static string? NodeName(TreeSitter.Node node)
    {
        var nameField = node.Fields.FirstOrDefault(f => f.Key == "name");
        return nameField.Key != null ? nameField.Value.Text : null;
    }

    /// <summary>
    /// Mirrors the planned buildFullName package step: the dotted package declared at
    /// program level (Java package_declaration) or null when none exists.
    /// </summary>
    static string? ProgramPackage(TreeSitter.Node program)
    {
        foreach (var child in program.NamedChildren)
        {
            if (child.Type != "package_declaration")
                continue;

            // Grammar versions vary: prefer the package's last named child (the
            // scoped_identifier "a.b.c"), fall back to stripping the keyword and ';'.
            var pkg = child.NamedChildren.LastOrDefault();
            if (pkg is not null && !string.IsNullOrWhiteSpace(pkg.Text))
                return pkg.Text.Trim();

            var raw = child.Text?.Trim();
            if (raw is null)
                continue;
            const string keyword = "package ";
            if (raw.StartsWith(keyword, StringComparison.Ordinal))
                raw = raw[keyword.Length..].Trim().TrimEnd(';').Trim();
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        return null;
    }

    static TreeSitter.Node? FindFirst(TreeSitter.Node node, Func<TreeSitter.Node, bool> predicate)
    {
        if (predicate(node))
            return node;

        foreach (var child in node.NamedChildren)
        {
            var found = FindFirst(child, predicate);
            if (found is not null)
                return found;
        }

        return null;
    }

    static void DumpNode(TreeSitter.Node node, string indent = "", int depth = 0)
    {
        if (depth > 14)
        {
            Console.WriteLine($"{indent}...");
            return;
        }

        var fields = node.Fields.Count > 0
            ? " fields=[" + string.Join(", ", node.Fields.Select(f => $"{f.Key}='{Escape(f.Value.Text)}'")) + "]"
            : "";
        Console.WriteLine($"{indent}{node.Type} '{Escape(node.Text)}'{fields}");

        foreach (var child in node.NamedChildren)
            DumpNode(child, indent + "  ", depth + 1);
    }

    static string Escape(string s) =>
        s.Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");
}