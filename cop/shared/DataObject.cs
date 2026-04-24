using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Cop.Core;

/// <summary>
/// A flat, fixed-size binary record. Property values are stored inline in 8-byte slots.
/// Strings are packed as (offset &lt;&lt; 32 | length) referencing a shared UTF-8 string heap.
/// The struct is blittable (no managed refs) so that future out-of-proc providers
/// can overlay a <see cref="ReadOnlySpan{DataObject}"/> on a received byte[] via MemoryMarshal.Cast.
/// Layout follows the CLR MetadataReader pattern: compact storage, lazy string decode.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DataObject
{
    /// <summary>Maximum number of property slots per record.</summary>
    public const int MaxSlots = 24;

    /// <summary>Inline array of 24 eight-byte property slots (192 bytes total).</summary>
    [InlineArray(MaxSlots)]
    public struct PropertySlots { private long _e0; }

    /// <summary>
    /// The property slots. Each slot holds a long value:
    /// int/bool/long values stored directly; string values packed as (offset, length).
    /// Slot index = property position in the provider schema.
    /// </summary>
    public PropertySlots Slots;

    // ── Readers ──

    /// <summary>Reads a 32-bit integer from a slot.</summary>
    public readonly int GetInt32(int slot) => (int)Slots[slot];

    /// <summary>Reads a 64-bit integer from a slot.</summary>
    public readonly long GetInt64(int slot) => Slots[slot];

    /// <summary>Reads a boolean from a slot (0 = false, nonzero = true).</summary>
    public readonly bool GetBool(int slot) => Slots[slot] != 0;

    /// <summary>
    /// Decodes a UTF-8 string from the string heap. The slot contains
    /// (offset &lt;&lt; 32 | length) where offset/length address the heap.
    /// </summary>
    public readonly string GetString(int slot, byte[] stringHeap)
    {
        long packed = Slots[slot];
        if (packed == 0) return "";
        int offset = (int)(packed >> 32);
        int length = (int)(packed & 0xFFFFFFFF);
        return Encoding.UTF8.GetString(stringHeap, offset, length);
    }

    /// <summary>
    /// Returns the raw UTF-8 bytes for a string slot without allocating a CLR string.
    /// Useful for byte-level comparisons and pushdown filters.
    /// </summary>
    public readonly ReadOnlySpan<byte> GetStringBytes(int slot, ReadOnlySpan<byte> stringHeap)
    {
        long packed = Slots[slot];
        if (packed == 0) return [];
        int offset = (int)(packed >> 32);
        int length = (int)(packed & 0xFFFFFFFF);
        return stringHeap.Slice(offset, length);
    }
}

/// <summary>
/// A named collection of <see cref="DataObject"/> records sharing a UTF-8 string heap.
/// This is the unit returned by providers in the Objects format.
/// Multiple DataTables can share the same string heap (e.g., DiskFiles and Folders
/// from the same filesystem scan).
/// </summary>
public class DataTable
{
    public DataObject[] Records { get; }
    public byte[] StringHeap { get; }
    public string TypeName { get; }
    public int Count => Records.Length;

    public DataTable(DataObject[] records, byte[] stringHeap, string typeName)
    {
        Records = records;
        StringHeap = stringHeap;
        TypeName = typeName;
    }

    /// <summary>Reads a string property from a record by index and slot.</summary>
    public string GetString(int recordIndex, int slot)
        => Records[recordIndex].GetString(slot, StringHeap);

    /// <summary>Reads an int property from a record by index and slot.</summary>
    public int GetInt32(int recordIndex, int slot)
        => Records[recordIndex].GetInt32(slot);

    /// <summary>Reads a long property from a record by index and slot.</summary>
    public long GetInt64(int recordIndex, int slot)
        => Records[recordIndex].GetInt64(slot);

    /// <summary>Reads a bool property from a record by index and slot.</summary>
    public bool GetBool(int recordIndex, int slot)
        => Records[recordIndex].GetBool(slot);
}

/// <summary>
/// Lightweight evaluator bridge: references a single record in a <see cref="DataTable"/>
/// without boxing the 192-byte <see cref="DataObject"/> struct.
/// The evaluator works with <c>object</c> items — this class provides identity and
/// access to the underlying packed data with no struct copy.
/// </summary>
public sealed class DataObjectView
{
    public readonly DataTable Table;
    public readonly int Index;

    public DataObjectView(DataTable table, int index)
    {
        Table = table;
        Index = index;
    }
}

/// <summary>
/// Construction helper for building <see cref="DataObject"/> arrays.
/// Accumulates UTF-8 string bytes into a growable buffer; call <see cref="GetStringHeap"/>
/// once after all records are built to get the finalized byte[].
/// Share one builder across multiple collections to produce a single shared string heap.
/// </summary>
public class DataObjectBuilder
{
    private byte[] _buffer = new byte[4096];
    private int _position;
    private byte[]? _heap;

    /// <summary>
    /// Packs a string value into the string heap.
    /// Returns the packed (offset &lt;&lt; 32 | length) value to store in a DataObject slot.
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
    /// The returned byte[] is shared by all DataTables that use this builder.
    /// </summary>
    public byte[] GetStringHeap()
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
