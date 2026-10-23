# markdown

Parses Markdown files into ordered, content-preserving document trees, alongside
headings, links, sections, and fenced code-block collections.

## Usage

```cop
import markdown

let parsed = markdown.parse()
command MAIN = foreach parsed.Documents
    => '{item.File.Path}: {item.Children.Count} top-level nodes'
```

`markdown.parse('architecture.md')` reads one file. A directory argument reads
Markdown files recursively, preserving separate document roots in `Documents`.
Relative paths resolve against the analysis target (`-t`); absolute paths are
also accepted. Missing or unreadable paths fail rather than returning a clean,
empty document.

## Tree Model

Each `MarkdownNode` has `Kind`, `Text`, `Content`, `Level`, `Children`, inclusive
`StartLine` / `EndLine`, and a `File` backlink. Children retain document order.
Heading levels are 1-6; other nodes have level zero.

- `Text` is the node's own text. Structural document, list, list-item, and quote
  nodes contain their text in child nodes.
- `Content` preserves the node's Markdown source, with line endings normalized
  to LF. For a heading, this includes its complete section until the next heading
  of the same or shallower level, including all paragraphs and deeper headings.
- Kinds include `document`, `heading`, `paragraph`, `list`, `listItem`,
  `codeBlock`, `quote`, `table`, `html`, and `thematicBreak`.

The tree recognizes ATX and Setext headings, nested lists, fenced and indented
code, quotes, and pipe tables. Inline formatting remains text; tables remain
whole text blocks rather than cell models. This is a document-outline and
content model, not a Markdown renderer. The original document remains available
in the root's `Content`. Nesting beyond 128 levels fails explicitly.

## Markdown-Authored AI Rules

Use `ai.judge(markdown.parse('architecture.md'), codebase, 2)` to select level-two
sections as rules. Each section is one requirement, regardless of paragraph
count; parent headings and prose provide context. Markdown parsing itself has
no AI behavior and makes no model requests.
