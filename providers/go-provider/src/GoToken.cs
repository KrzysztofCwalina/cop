// Tokenizes Go source and projects declarations and statements into the code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

// Col is 1-based column of the first character of the token on its line.
internal record struct GoToken(GoTokenKind Kind, string Value, int Line, int Col, int Start, int End);
