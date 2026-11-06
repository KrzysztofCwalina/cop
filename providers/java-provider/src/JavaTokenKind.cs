// Tokenizes Java source and projects its declarations and statements into the code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal enum JavaTokenKind
{
    Identifier, Keyword, Punctuation, StringLiteral, NumberLiteral,
    LineComment, BlockComment, DocComment, Annotation, Eof
}
