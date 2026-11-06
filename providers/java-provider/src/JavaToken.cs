// Tokenizes Java source and projects its declarations and statements into the code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal record struct JavaToken(JavaTokenKind Kind, string Value, int Line, int Start, int End);
