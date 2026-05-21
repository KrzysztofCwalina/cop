using System.Text;

namespace Cop.Core;

/// <summary>
/// Accumulates UTF-8 string bytes into a growable buffer.
/// Shared across multiple <see cref="DataTableBuilder"/> instances
/// so all tables in a <see cref="DataStore"/> reference the same string heap.
/// </summary>
public class StringHeapBuilder
{
    private byte[] _buffer = new byte[4096];
    private int _position;
    private byte[]? _heap;

    /// <summary>
    /// Packs a string value into the string heap.
    /// Returns the packed (offset &lt;&lt; 32 | length) value to store in a slot.
    /// Returns 0 for null/empty strings.
    /// </summary>
    public long PackString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;

        int byteCount = Encoding.UTF8.GetByteCount(value);
        EnsureCapacity(byteCount);

        int offset = _position;
        Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _position);
        _position += byteCount;

        return ((long)offset << 32) | (uint)byteCount;
    }

    /// <summary>
    /// Returns the finalized string heap. Call once after all strings are packed.
    /// </summary>
    public byte[] Build()
    {
        if (_heap != null) return _heap;
        _heap = new byte[_position];
        Buffer.BlockCopy(_buffer, 0, _heap, 0, _position);
        return _heap;
    }

    private void EnsureCapacity(int needed)
    {
        int required = _position + needed;
        if (required <= _buffer.Length) return;
        int newSize = Math.Max(_buffer.Length * 2, required);
        Array.Resize(ref _buffer, newSize);
    }
}
