// Selects pinned frontend or source-only Rust parsing for the shared code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal record struct RustToken(RustTokenKind Kind, string Value, int Line, int Start, int End);
