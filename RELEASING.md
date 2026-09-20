# Releasing CodeMemory

## Overview

CodeMemory is released in two channels that share the same version number:

1. **GitHub Release** — self-contained binaries for each platform (triggered by git tag)
2. **NPM package** (`@uworx/code-memory`) — CLI wrapper that downloads the platform-specific binary on install

## Step-by-Step

### 1. Update version in NPM package

```bash
# Edit packages/code-memory/package.json and bump "version"
# (must be semver, e.g. 1.1.0)
```

### 2. Review public docs

Bump/refresh the public docs to describe what changed since the last release — they ship with the code:

```bash
# See what changed in code since the last tag
git log --oneline v<PREVIOUS_TAG>..HEAD
git diff --stat v<PREVIOUS_TAG>..HEAD
```

Review these files (they live at the repo root and publish with the repo):

- `README.md` — capabilities, feature highlights, quick start
- `ARCHITECTURE.md` — system architecture, data flow, layering
- `GETTING-STARTED.md` — install, configure, query a repo
- `CONTAINER-README.md` — container / Docker Compose deployment

Reflect user-facing changes only where they make sense against the existing structure — no filler, and each piece of info belongs in exactly one of these files, not repeated.

### 3. Tag and push to trigger the binary build

```bash
git tag v1.1.0
git push origin v1.1.0
```

This triggers the **Release** GitHub Action (`.github/workflows/cd.yml`) which:
- Builds the project for each platform in the build matrix (`win-x64` initially)
- Zips the publish folder (including Tree-sitter native binaries) into `code-memory-{rid}.zip`
- Creates a GitHub Release with the attached bundle

### 4. Wait for the build to finish

Verify the release exists at:
`https://github.com/khurram-uworx/CodeMemory/releases/tag/v1.1.0`

The NPM postinstall script downloads from:
`https://github.com/khurram-uworx/CodeMemory/releases/download/v1.1.0/code-memory-win-x64.zip`

### 5. Publish the NPM package

```bash
cd packages/code-memory
npm publish
```

> The NPM package must be published **after** the GitHub Release exists, because its `postinstall` script downloads the binary from the release.

### 6. Verify

```bash
npx @uworx/code-memory --version
# or point at a repo:
npx @uworx/code-memory --repo /some/project
```

## Important Notes

- **Version alignment**: The git tag (`v1.1.0`) and NPM version (`1.1.0`) must match (tag has `v` prefix, NPM does not).
- **`@uworx` scope**: Requires `--access public` when publishing (set in `publishConfig`).
- **Platform support matrix**: Add new RIDs to the build matrix in `cd.yml` and the `ridMap` in `download-binary.js`.
