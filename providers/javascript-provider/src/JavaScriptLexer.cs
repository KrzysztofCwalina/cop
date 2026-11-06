// Tokenizes JavaScript and TypeScript while retaining comment and source positions.

using System.Text;

namespace Cop.Providers.SourceParsers;

internal enum JsTok { Word, Number, Str, Regex, Punct, LineComment, BlockComment, EOF }
