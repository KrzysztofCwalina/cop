// Connects Rust body parsing to the enclosing parser's diagnostics and nested functions.

namespace Cop.Providers.SourceParsers;

internal sealed record RustBodyParserCallbacks(
    Action<RustToken> ReportNestingLimit, Func<int, int> ParseNestedFunction);
