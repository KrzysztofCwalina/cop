# Installing cop

cop ships as self-contained single-file executables — no .NET runtime needed on the target machine.

## Quick Install

Run the publish script from the repo root:

```powershell
install/publish.ps1
```

This builds `cop` and `copweb` for all supported platforms:

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

## Usage

Add the platform subfolder to your PATH, or invoke directly:

```bash
# Windows
install\win-x64\cop.exe run checks.cop

# Linux / macOS
install/linux-x64/cop run checks.cop
```

## VS Code Extension

The `vscode-cop/` subfolder contains the VS Code syntax highlighting extension for `.cop` files. It provides syntax coloring for keywords, strings, template expressions, comments, and more — works with both light and dark themes.

### Install from GitHub (no clone needed)

Download the extension folder directly and copy it into your VS Code extensions directory:

```powershell
# Windows (PowerShell)
$dest = "$env:USERPROFILE\.vscode\extensions\cop.cop-language-0.1.0"
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
dest="$HOME/.vscode/extensions/cop.cop-language-0.1.0"
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
cp -r install/vscode-cop ~/.vscode/extensions/cop.cop-language-0.1.0
```

After installing, reload VS Code (`Ctrl+Shift+P` → `Developer: Reload Window`).
