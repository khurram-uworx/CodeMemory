# Import Context in Semantic Chunks — Known Limitations

`SemanticChunker.extractFileContext` captures import/using/header lines from each file and prepends them to type-level chunks. This gives semantic search visibility into what a file depends on.

## Current Coverage

| Language | Prefixes matched | Example |
|----------|-----------------|---------|
| C# | `using`, `namespace` | `using System.Text;` |
| TypeScript / JavaScript | `import`, `export`, `require(`, `/// <reference`, `module` | `import { Foo } from './foo'` |
| Java | `import`, `package` | `import java.util.List;` |
| Python | `import`, `from` | `from typing import Optional` |
| Go | `import`, `package` | `import "fmt"` |
| Rust | `use`, `pub`, `mod` | `use std::collections::HashMap` |

## Known Gap: Block-Style Imports

Go supports block-style import groups:

```go
import (
    "fmt"
    "os"
)
```

The opening line `import (` does **not** match `StartsWith("import ")` because it lacks a trailing space before the parenthesis. Individual lines inside the block (`"fmt"`, `"os"`) also aren't matched since they don't start with `import`.

**Impact:** Go files using block imports lose their dependency context in type chunks. Single-line `import "pkg"` declarations are unaffected.

**Same limitation applies to:** any language that splits imports across multiple lines (TypeScript dynamic `import()`, Python parenthesized imports, etc.).

## Not Covered

- **Exports** (`pub use` or `pub fn` lines are captured as file context by the Rust `pub` prefix, but not broken down into individual symbols)
- **C/C++ `#include`** — not yet supported
- **Ruby `require` / `include`** — not yet supported

## Future Direction

Making import detection fully language-aware for block-style and multi-line imports is a low-priority improvement. The current line-level `StartsWith` matching covers the common single-line import forms for all supported languages. Fixing block imports would require either:
1. Tracking open/close delimiters (`(` / `)`, `{` / `}`) across lines, or
2. A light skip-ahead parser for each language's import syntax.

Neither is justified at present given the low frequency of block-style imports in most codebases.
