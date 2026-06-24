# xml

Analyze XML files and XML-based project/config files by walking documents into flat element and
attribute collections with local names, dotted paths, values, line numbers, file links, and source
positions.

Matched extensions are `.xml`, `.csproj`, `.props`, `.targets`, `.config`, and `.nuspec`.
Malformed files are skipped.

## Types

- **XmlElement** — fields: `Name` (local name), `Path` (dotted path from the root, e.g.
  `Project.PropertyGroup.Nullable`), `Value` (direct text content, trimmed; empty when only child
  elements are present), `Line`, `File`, `Source`.
- **XmlAttribute** — fields: `Name`, `Value`, `ElementName`, `ElementPath`, `Line`, `File`, `Source`.

Both types conform to `TextFilePosition`, so they work directly with `toError`, `toWarning`, and
`toInfo` from `code-analysis`.

## Usage

```cop
import xml

let content = xml.parse()
let projectFiles = xml.parse('src/MyProject.csproj')
```

## Example — forbid floating NuGet versions

```cop
import xml
import code

predicate isFloatingPackageVersion(XmlAttribute) =>
    XmlAttribute.ElementName:equals('PackageReference')
    && XmlAttribute.Name:equals('Version')
    && XmlAttribute.Value:contains('*')

let floating = xml.parse().Attributes:isFloatingPackageVersion
    :toError('PackageReference {item.ElementPath} uses floating version {item.Value}')

command MAIN = CHECK(floating)
```

See `samples/floating-package-versions.cop` for a runnable version.
