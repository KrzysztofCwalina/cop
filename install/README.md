# Installing cop

cop ships as self-contained single-file executables — no .NET runtime needed on the target machine.

## Quick Install

Run the publish script from the repo root:

```powershell
install/publish.ps1
```

This builds `cop` for all supported platforms:

| Subfolder | OS | Architecture |
|---|---|---|
| `win-x64/` | Windows | x64 |
| `win-arm64/` | Windows | ARM64 |
| `linux-x64/` | Linux | x64 |
| `linux-arm64/` | Linux | ARM64 |
| `osx-x64/` | macOS | x64 (Intel) |
| `osx-arm64/` | macOS | ARM64 (Apple Silicon) |

## Single Platform

To build for just your platform:

```powershell
install/publish.ps1 -Runtimes win-x64
```

The base executable has no external language-runtime requirement. Publishing the base
for any of these platforms requires .NET, not Rust, Cargo, Python, Node.js, Go, or Java.
Structural analysis remains available when optional language tools are absent, with
explicit warnings where richer analysis is unavailable. Already installed optional
toolchains can supply compiler-backed analysis; Cop never installs them automatically.

## Optional Rust language bundle

The native Rust frontend and standard-library sources are **not embedded in the base
executable**. Download the matching optional `cop-rust-<rid>.zip` release asset and
extract its `Cop.RustFrontend.Bundle.zip` beside `cop`/`cop.exe` to opt in. Remove that
file to opt out; previously extracted cache files do not enable the frontend.
Updating the base executable neither downloads nor installs optional language bundles.

To build only the optional distribution:

```powershell
install/publish-rust-bundle.ps1 -Runtime win-x64
```

Only this explicit build requires Cargo/Rust, the requested Rust target, and its native
linker. Cross-publishing the base does not. The optional bundle contains the pinned
helper, its third-party licenses, and checksum-verified Rust 1.99.1 standard-library
sources. The source archive is downloaded at build time and cached under
`install/native-bundles/inputs/`. To supply it locally and forbid Cargo network access:

```powershell
install/publish-rust-bundle.ps1 -Runtime win-x64 -RustSourceArchive C:\inputs\rust-src-1.99.1.tar.xz -Offline
```

Cargo dependencies must already be cached for `-Offline`.
`install/prepare-rust-bundle.ps1 -Runtime win-x64` prepares the inner payload only.
Direct `dotnet publish` of the CLI does not require any Rust payload or property.
With the optional archive installed, Cop lazily extracts its versioned payload into
its user cache, without downloads, writes to the analyzed project, or a rust-analyzer
installation. Matching installed Rust sources take precedence; bundled standard-library
sources require an exactly compatible compiler release and commit. Incompatible
context produces explicit diagnostics rather than guessed bindings.

For the host's runtime identifier, publishing verifies and executes small C#, Python,
JavaScript, Go, Java, Rust, and Cop analysis programs using the actual executable and
freshly built provider DLLs. Each must retain exactly one known declaration, method,
and call on both cold and warm runs. Malformed input must produce a visible diagnostic
and a nonzero exit code. Missing optional toolchains may emit fallback warnings; they
must not hide these baseline facts or turn malformed input into success.
The base gate also checks exact receiver, result, and selected-callable facts for C#,
plus a scalar-receiver call in Cop source, on repeated executions. The publishing
gate runs every provider from a copy of the executable alone, proving that framework
metadata does not depend on build-output sidecars. CI separately builds and verifies
the opted-in Rust distribution, including exact semantic facts.

CI provisions Node.js and a pinned TypeScript compiler before publishing and testing,
so JavaScript semantic regressions exercise the compiler rather than the structural
fallback. The compiler remains optional for end-user structural analysis.

A failed build or smoke test stops publishing before archive creation or PATH updates.
`-SkipBuild` also runs this gate for the host runtime. Cross-platform archives still
need functional testing on their target OS; generating or checking archive metadata
on Windows does not verify Linux/macOS execution.

To run only the provider smoke against an existing local build without publishing or
modifying PATH:

```powershell
install/provider-smoke.ps1 -Executable cop/cli/bin/Debug/net10.0/cop.exe
```

For a focused Rust structural and semantic check of the portable executable:

```powershell
install/provider-smoke.ps1 -Executable install/win-x64/cop.exe -Languages rust -RustBundlePath install/native-bundles/win-x64/bundle.zip
```

Language selection limits manual smoke runs only; publishing still checks every provider.

## Usage

Add the platform subfolder to your PATH, or invoke directly:

```bash
# Windows
install\win-x64\cop.exe checks.cop

# Linux / macOS
install/linux-x64/cop checks.cop
```

## VS Code Extension

The `vscode-cop/` subfolder contains the VS Code extension for `.cop` files. It provides:

- **Syntax highlighting** — keywords, strings, template expressions, comments, and more (light & dark themes)
- **IntelliSense completions** — context-aware suggestions triggered by `.`, `:`, and space:

| Context | What's offered |
|---|---|
| `runtime::` | Runtime type names (Codebase, Filesystem) |
| `import ` | Known package names |
| `Name:` | Type-aware predicates (string → equals/startsWith/…, collection → any/none/…) |
| `Code.` | Properties of the resolved type (Types, Statements, Lines, Files) |
| `=> ` | Actions (PRINT, SAVE) and violation transforms (toError, toWarning, …) |
| Line start | Keywords, let bindings, runtime variables |
| `export ` | Declaration keywords (predicate, function, type, let, …) |

The extension also discovers **user-defined predicates, functions, and let bindings** in the current file and offers them as completions.

### Install from GitHub (no clone needed)

Download the extension folder directly and copy it into your VS Code extensions directory:

```powershell
# Windows (PowerShell)
$dest = "$env:USERPROFILE\.vscode\extensions\cop.cop-language-0.2.0"
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
gh repo clone KrzysztofCwalina/cop -- --depth 1 --filter=blob:none --sparse --no-checkout "$env:TEMP\cop-ext"
Push-Location "$env:TEMP\cop-ext"
git sparse-checkout set install/vscode-cop
git checkout
Pop-Location
Copy-Item "$env:TEMP\cop-ext\install\vscode-cop" $dest -Recurse
Remove-Item "$env:TEMP\cop-ext" -Recurse -Force
```

```bash
# Linux / macOS
dest="$HOME/.vscode/extensions/cop.cop-language-0.2.0"
rm -rf "$dest"
tmp=$(mktemp -d)
gh repo clone KrzysztofCwalina/cop -- --depth 1 --filter=blob:none --sparse --no-checkout "$tmp/cop-ext"
cd "$tmp/cop-ext" && git sparse-checkout set install/vscode-cop && git checkout && cd -
cp -r "$tmp/cop-ext/install/vscode-cop" "$dest"
rm -rf "$tmp/cop-ext"
```

### Install from a local clone

```bash
# From the repo root
cp -r install/vscode-cop ~/.vscode/extensions/cop.cop-language-0.2.0
```

After installing, reload VS Code (`Ctrl+Shift+P` → `Developer: Reload Window`).
