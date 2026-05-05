## markdown

Markdown document structural analysis. &nbsp; `import markdown`

**Source:** Built-in provider (`Cop.Providers.Markdown`)

---

### Collections

`Markdown` is the top-level object containing:

| Collection | Type | Description |
|---|---|---|
| `Markdown.Headings` | [`[Heading]`](#heading) | All headings across `.md` files |
| `Markdown.Links` | [`[Link]`](#link) | All links (inline, reference, autolinks) |
| `Markdown.Sections` | [`[Section]`](#section) | Heading-delimited content sections |
| `Markdown.FenceBlocks` | [`[FenceBlock]`](#fenceblock) | Fenced code blocks |

---

### Types

#### Heading

| Property | Type | Description |
|---|---|---|
| `Text` | `string` | Heading text content |
| `Level` | `int` | Heading level (1–6) |
| `Line` | `int` | Source line number |
| `File` | `File?` | Containing file |
| `Source` | `string` | Raw source line |

#### Link

| Property | Type | Description |
|---|---|---|
| `Url` | `string` | Link target URL |
| `Text` | `string?` | Display text (nic for bare URLs) |
| `Line` | `int` | Source line number |
| `File` | `File?` | Containing file |
| `Source` | `string` | Raw source text |

#### Section

| Property | Type | Description |
|---|---|---|
| `Heading` | `string` | Section heading text |
| `Level` | `int` | Heading level (1–6) |
| `Content` | `string` | Full section content (including sub-headings) |
| `StartLine` | `int` | First line of section |
| `EndLine` | `int` | Last line of section |
| `File` | `File?` | Containing file |
| `Source` | `string` | Raw section source |

#### FenceBlock

| Property | Type | Description |
|---|---|---|
| `Language` | `string?` | Language tag (e.g. `csharp`, `json`) |
| `Tag` | `string?` | Additional tag after language |
| `StartLine` | `int` | Opening fence line number |
| `EndLine` | `int` | Closing fence line number |
| `Content` | `string` | Code block content |
| `ContentHash` | `string` | Hash of content (for change detection) |
| `File` | `File?` | Containing file |
| `Source` | `string` | Full fence block source |

---

### Examples

```ruby
import markdown

# Find broken heading hierarchy (h3 without preceding h2)
predicate skipsLevel(Heading) => Heading.Level > 2

# Find external links
predicate external(Link) => Link.Url:startsWith('http')
let external-links = Markdown.Links:external

# Find undocumented code blocks (no language tag)
predicate untagged(FenceBlock) => FenceBlock.Language:empty
let untagged-fences = Markdown.FenceBlocks:untagged:toWarning('Code block without language tag at line {item.StartLine}')
```
