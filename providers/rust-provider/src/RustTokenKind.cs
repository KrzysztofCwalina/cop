// Selects pinned frontend or source-only Rust parsing for the shared code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal enum RustTokenKind
{
    Identifier, Keyword, Punctuation, StringLiteral, NumberLiteral,
    LineComment, DocComment, BlockComment, Attribute, Lifetime, Eof
}
