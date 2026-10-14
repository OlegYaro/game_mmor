using Core.Determinism;

namespace Core.Tests.Determinism;

public sealed class StateWriterTests
{
    [Fact]
    [Trait("Req", "NFR-04")]
    [Trait("Req", "FR-SAVE-04")]
    public void ByteLayoutMatchesGolden()
    {
        var writer = new StateWriter();

        writer.WriteByte(0xAB);
        writer.WriteBool(true);
        writer.WriteBool(false);
        writer.WriteInt32(-2);
        writer.WriteUInt32(0x01020304u);
        writer.WriteInt64(0x0102030405060708L);
        writer.WriteInt64(long.MinValue);
        writer.WriteUInt64(0xFFFFFFFFFFFFFFFEUL);
        writer.WriteString("Hé");
        writer.WriteString("");
        writer.WriteBytes([0x01, 0x02]);

        byte[] expected =
        [
            0xAB,
            0x01,
            0x00,
            0xFE, 0xFF, 0xFF, 0xFF,
            0x04, 0x03, 0x02, 0x01,
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80,
            0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x03, 0x00, 0x00, 0x00, 0x48, 0xC3, 0xA9,
            0x00, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00, 0x01, 0x02,
        ];
        Assert.Equal(expected, writer.ToArray());
        Assert.Equal(expected.Length, writer.Length);
        Assert.Equal(expected, writer.WrittenSpan.ToArray());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void BufferGrowsFromZeroCapacityAndKeepsContent()
    {
        var writer = new StateWriter(0);

        for (int i = 0; i < 1_000; i++)
        {
            writer.WriteInt32(i);
        }

        byte[] bytes = writer.ToArray();
        Assert.Equal(4_000, bytes.Length);
        for (int i = 0; i < 1_000; i++)
        {
            Assert.Equal(i, BitConverter.ToInt32(bytes, i * 4));
        }
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void ResetReusesWriterFromStart()
    {
        var writer = new StateWriter(4);
        writer.WriteUInt64(ulong.MaxValue);

        writer.Reset();
        writer.WriteByte(7);

        Assert.Equal(1, writer.Length);
        Assert.Equal(new byte[] { 7 }, writer.ToArray());
    }

    [Fact]
    [Trait("Req", "NFR-04")]
    public void InvalidInputsThrow()
    {
        var writer = new StateWriter();

        Assert.Throws<ArgumentOutOfRangeException>(() => new StateWriter(-1));
        Assert.Throws<ArgumentNullException>(() => writer.WriteString(null!));
        Assert.ThrowsAny<ArgumentException>(() => writer.WriteString("\uD800"));
    }
}
