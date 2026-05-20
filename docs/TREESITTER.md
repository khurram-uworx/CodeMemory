# Tree-Sitter Grammar Limitations

## C++ `template_declaration` — No `name` / `declaration` Fields

### Problem

The bundled tree-sitter-cpp grammar (TreeSitter.DotNet v1.3.0) parses `template<typename T> class Container {};` into a valid AST with a `template_declaration` node. However, this node **does not expose `name:` or `declaration:` as named fields** in the grammar.

This means tree-sitter query patterns like:

```
(template_declaration name: (_) @name) @node
(template_declaration declaration: (_) @name) @node
```

**fail to compile** with `Query error` — the query engine rejects field references that don't exist in the grammar. This is a hard error that crashes the entire query, not a silent no-match.

### Why

`template_declaration` stores its inner declaration (class_specifier, function_definition, etc.) as a **positional child**, not as a named field. The `template_parameter_list` is exposed as a `parameters` field, but the inner declaration has no named field.

From the grammar perspective, the template node structure is:

```
template_declaration
  template        [unnamed]
  <               [unnamed]
  template_parameter_list  [field: parameters]
  >               [unnamed]
  class_specifier [positional child, no named field]
  ...
```

Because the inner declaration lacks a named field, capturing it via a field-anchored query is impossible. We cannot upgrade the bundled grammar — it's compiled into the `TreeSitter.DotNet` v1.3.0 native binaries.

### What We Do Instead

**Inner symbols are captured by existing patterns.** The inner `class_specifier`, `function_definition`, `alias_declaration`, etc. are matched independently by their own query patterns, regardless of whether they sit inside a `template_declaration`. This means:

| Template code | What's captured | Kind |
|---|---|---|
| `template<typename T> class Container {};` | `Container` | Class |
| `template<typename T> T max(T a, T b) {}` | `max(T a, T b)` | Function |
| `template<typename T> using MyVec = std::vector<T>;` | `MyVec` | TypeAlias |
| `template<typename T> constexpr T pi = T(3.14);` | `pi` | Variable |

**Template parameters are added as modifiers.** The template parameter list text is extracted from the parent `template_declaration.parameters` field and added as a modifier string (e.g., `template<typename T>`) on the inner symbol. See `extractModifiers` in `TreeSitterSymbolExtractor.cs`.

### Current Limitations

- Template parameters are stored as a plain string modifier (e.g., `"template<typename T>"`) — they are not structured metadata
- The `template_declaration` node itself is not indexed as a separate symbol — only the inner declaration is
- Variable declarations inside `template_declaration` are indexed only when the parent is a `template_declaration` directly nested in `translation_unit` (handled by extended parent check in `processMatch`)
