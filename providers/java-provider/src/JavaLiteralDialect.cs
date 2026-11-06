// Distinguishes Java character literals from Gradle's single-quoted strings.

namespace Cop.Providers.SourceParsers;

internal enum JavaLiteralDialect
{
    Java,
    Gradle
}
