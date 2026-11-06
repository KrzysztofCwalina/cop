// Exchanges versioned rust-analyzer requests and attaches semantic source facts.

using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal sealed class RustSourcePositions
{
    private readonly int[] _characters;
    private readonly int[] _lines;
    private readonly int[] _columns;
    /// <summary>
    /// Indexes UTF-8 byte offsets against source characters and line starts.
    /// </summary>
    public RustSourcePositions(string text)
    {
        int bytes = Encoding.UTF8.GetByteCount(text);
        _characters = new int[bytes + 1]; _lines = new int[bytes + 1]; _columns = new int[bytes + 1];
        int offset = 0, character = 0, line = 1, column = 1;
        foreach (Rune rune in text.EnumerateRunes())
        {
            for (int i = 0; i < rune.Utf8SequenceLength; i++)
            { _characters[offset + i] = character; _lines[offset + i] = line; _columns[offset + i] = column; }
            offset += rune.Utf8SequenceLength; character += rune.Utf16SequenceLength;
            if (rune.Value == '\n') { line++; column = 1; } else
            {
                column += rune.Utf16SequenceLength;
            }
        }
        _characters[bytes] = character; _lines[bytes] = line; _columns[bytes] = column;
    }
    /// <summary>Converts a frontend byte offset to its source character index.</summary>
    public int Character(int offset) => _characters[offset];
    /// <summary>
    /// Converts frontend byte offsets into a line-and-column source span.
    /// </summary>
    public (int Line, int Column, int EndLine, int EndColumn) Span(int start, int end) =>
        (_lines[start], _columns[start], _lines[end], _columns[end]);
}
