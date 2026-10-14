using System.Buffers.Binary;
using System.Text;

namespace Core.Determinism;

/// <summary>
/// Canonical binary writer for simulation state: fixed-width little-endian integers, booleans as one byte,
/// strings and byte blobs as an Int32 length prefix followed by the bytes. Output is identical on every platform.
/// </summary>
/// <remarks>Reuse one writer per tick with <see cref="Reset"/> to avoid allocations.</remarks>
public sealed class StateWriter
{
    private const int DefaultCapacity = 256;

    // Throws on lone surrogates instead of silently substituting U+FFFD.
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private byte[] _buffer;
    private int _length;

    /// <summary>Creates a writer with the given initial buffer capacity.</summary>
    public StateWriter(int initialCapacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _buffer = initialCapacity == 0 ? [] : new byte[initialCapacity];
    }

    /// <summary>Number of bytes written since creation or the last <see cref="Reset"/>.</summary>
    public int Length => _length;

    /// <summary>The bytes written so far. Invalidated by the next write or <see cref="Reset"/>.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>Forgets written bytes but keeps the buffer.</summary>
    public void Reset() => _length = 0;

    /// <summary>Copies the written bytes into a new array.</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();

    /// <summary>Writes one byte.</summary>
    public void WriteByte(byte value) => Reserve(1)[0] = value;

    /// <summary>Writes <c>1</c> for true and <c>0</c> for false.</summary>
    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    /// <summary>Writes 4 bytes, little-endian.</summary>
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(sizeof(int)), value);

    /// <summary>Writes 4 bytes, little-endian.</summary>
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(sizeof(uint)), value);

    /// <summary>Writes 8 bytes, little-endian.</summary>
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(sizeof(long)), value);

    /// <summary>Writes 8 bytes, little-endian.</summary>
    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(sizeof(ulong)), value);

    /// <summary>Writes the UTF-8 byte count as Int32, then the UTF-8 bytes (no BOM, no terminator).</summary>
    /// <exception cref="ArgumentException">The string contains a lone surrogate.</exception>
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = _strictUtf8.GetByteCount(value);
        WriteInt32(byteCount);
        _strictUtf8.GetBytes(value, Reserve(byteCount));
    }

    /// <summary>Writes the length as Int32, then the bytes.</summary>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteInt32(value.Length);
        value.CopyTo(Reserve(value.Length));
    }

    private Span<byte> Reserve(int count)
    {
        int required = _length + count;
        if (required > _buffer.Length)
        {
            int newSize = Math.Max(required, Math.Max(_buffer.Length * 2, DefaultCapacity));
            Array.Resize(ref _buffer, newSize);
        }

        Span<byte> span = _buffer.AsSpan(_length, count);
        _length = required;
        return span;
    }
}
