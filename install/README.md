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

Publishing also builds the pinned Rust analysis helper and embeds it, its third-party
licenses, and the checksum-verified Rust 1.99.1 standard-library sources inside `cop`.
Building requires Cargo/Rust and the native linker and Rust target for each requested
RID; cross-publishing .NET alone is not sufficient. Build each platform on a suitable
host or provision its cross-toolchain first. The pinned source archive is downloaded
at build time and cached under `install/native-bundles/inputs/`.

To supply the official source archive locally and forbid Cargo network access:

```powershell
install/publish.ps1 -Runtimes win-x64 -RustSourceArchive C:\inputs\rust-src-1.99.1.tar.xz -RustOffline
```

Cargo dependencies must already be cached for `-RustOffline`; this option does not
disable .NET restore. `install/prepare-rust-bundle.ps1 -Runtime win-x64` can prepare
just the embedded payload. Direct `dotnet publish` of the CLI requires its returned
ZIP path through `-p:RustFrontendBundlePath=...`; ordinary development builds do not.
The installed executable lazily extracts its own versioned payload into Cop's user
cache, without downloads, writes to the analyzed project, or a separate rust-analyzer
installation. Matching installed Rust sources take precedence; bundled standard-library
sources require an exactly compatible compiler release and commit. Incompatible
context produces explicit diagnostics rather than guessed bindings.

For the host's runtime identifier, publishing verifies and executes small C#, Python,
JavaScript, Go, Java, Rust, and Cop analysis programs using the actual executable and
freshly built provider DLLs. Each must retain exactly one known declaration, method,
and call on both cold and warm runs. Malformed input must produce a visible diagnostic
and a nonzero exit code. Missing optional toolchains may emit fallback warnings; they
must not hide these baseline facts or turn malformed input into success.
The gate also checks exact receiver, result, and selected-callable facts for C# and
Rust, plus a scalar-receiver call in Cop source, on repeated executions. The publishing
gate runs every provider from a copy of the executable alone, proving that framework
metadata and the embedded native payload do not depend on build-output sidecars.

A failed build or smoke test stops publishing before archive creation or PATH updates.
`-SkipBuild` also runs this gate for the host runtime. Cross-platform archives still
need functional testing on their target OS; generating or checking archive metadata
on Windows does not verify Linux/macOS execution.

To run only the provider smoke against an existing local build without publishing or
modifying PATH:

```powershell
install/provider-smoke.ps1 -Executable cop/cli/bin/Debug/net10.0/cop.exe
```

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
