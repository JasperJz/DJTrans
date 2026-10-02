using DJTrans.Core.Thumbs;

namespace DJTrans.Core.Tests.Thumbs;

/// <summary>
/// DngPreviewExtractor 的单元测试：所有 TIFF/DNG 样本均由测试内手写字节组装，不依赖外部样本文件。
/// TIFF 布局约定：头 8 字节（II/MM + magic 42 + IFD0 偏移），IFD0 固定从偏移 8 开始。
/// </summary>
public class DngPreviewExtractorTests
{
    private const ushort TagStripOffsets = 0x111;
    private const ushort TagStripByteCounts = 0x117;
    private const ushort TagSubIfds = 0x14A;

    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;

    // ---------- 用例 1：IFD0 直接携带 strip（含 SHORT 类型的字节数标签） ----------
    [Fact]
    public void ExtractsPreview_FromIfd0DirectStrip()
    {
        byte[] jpeg = MakeFakeJpeg(payloadBytes: 20);
        var tiff = new TiffBuilder(littleEndian: true);

        const int ifd0Offset = 8;
        const int ifd0Size = 2 + 2 * 12 + 4; // 2 条目 + 计数 + nextIFD
        uint jpegOffset = (uint)(ifd0Offset + ifd0Size);

        tiff.WriteIfd(
            (TagStripOffsets, TypeLong, 1u, tiff.InlineU32(jpegOffset)),
            (TagStripByteCounts, TypeShort, 1u, tiff.InlineShorts((ushort)jpeg.Length)));
        tiff.WriteBytes(jpeg);

        byte[]? result = DngPreviewExtractor.TryExtractPreviewJpeg(new MemoryStream(tiff.ToArray(ifd0Offset)));

        Assert.NotNull(result);
        Assert.Equal(jpeg, result);
    }

    // ---------- 用例 2：SubIFDs(0x14A) → 预览 IFD → strip 对 ----------
    [Fact]
    public void ExtractsPreview_FromSubIfd()
    {
        byte[] jpeg = MakeFakeJpeg(payloadBytes: 18);
        var tiff = new TiffBuilder(littleEndian: true);

        const int ifd0Offset = 8;
        const int ifd0Size = 2 + 1 * 12 + 4;
        int previewIfdOffset = ifd0Offset + ifd0Size;
        const int previewIfdSize = 2 + 2 * 12 + 4;
        uint jpegOffset = (uint)(previewIfdOffset + previewIfdSize);

        tiff.WriteIfd((TagSubIfds, TypeLong, 1u, tiff.InlineU32((uint)previewIfdOffset)));
        tiff.WriteIfd(
            (TagStripOffsets, TypeLong, 1u, tiff.InlineU32(jpegOffset)),
            (TagStripByteCounts, TypeLong, 1u, tiff.InlineU32((uint)jpeg.Length)));
        tiff.WriteBytes(jpeg);

        byte[]? result = DngPreviewExtractor.TryExtractPreviewJpeg(new MemoryStream(tiff.ToArray(ifd0Offset)));

        Assert.NotNull(result);
        Assert.Equal(jpeg, result);
    }

    // ---------- 用例 3：多段 StripOffsets 按数组顺序拼接 ----------
    [Fact]
    public void ExtractsPreview_MultiStrip_ConcatenatesInArrayOrder()
    {
        byte[] jpeg = MakeFakeJpeg(payloadBytes: 22); // 26 字节，不等长两段
        byte[] strip1 = jpeg[..10];
        byte[] strip2 = jpeg[10..];
        var tiff = new TiffBuilder(littleEndian: true);

        const int ifd0Offset = 8;
        const int ifd0Size = 2 + 2 * 12 + 4;
        int offsetsArray = ifd0Offset + ifd0Size;        // LONG×2 外部数组
        int countsArray = offsetsArray + 8;              // LONG×2 外部数组
        int strip2Offset = countsArray + 8;              // 物理上先放第二段……
        int strip1Offset = strip2Offset + strip2.Length; // ……再放第一段，制造与数组顺序相反的物理布局

        tiff.WriteIfd(
            (TagStripOffsets, TypeLong, 2u, tiff.InlineU32((uint)offsetsArray)),
            (TagStripByteCounts, TypeLong, 2u, tiff.InlineU32((uint)countsArray)));

        int writtenOffsets = tiff.WriteLongs((uint)strip1Offset, (uint)strip2Offset);
        int writtenCounts = tiff.WriteLongs((uint)strip1.Length, (uint)strip2.Length);
        Assert.Equal(offsetsArray, writtenOffsets);
        Assert.Equal(countsArray, writtenCounts);

        tiff.WriteBytes(strip2);
        tiff.WriteBytes(strip1);

        byte[]? result = DngPreviewExtractor.TryExtractPreviewJpeg(new MemoryStream(tiff.ToArray(ifd0Offset)));

        Assert.NotNull(result);
        // 结果必须是 strip1+strip2（数组顺序），而非文件物理顺序
        Assert.Equal(jpeg, result);
    }

    // ---------- 用例 4：MM（大端）字节序 ----------
    [Fact]
    public void ExtractsPreview_BigEndian()
    {
        byte[] jpeg = MakeFakeJpeg(payloadBytes: 16);
        var tiff = new TiffBuilder(littleEndian: false);

        const int ifd0Offset = 8;
        const int ifd0Size = 2 + 1 * 12 + 4;
        int previewIfdOffset = ifd0Offset + ifd0Size;
        const int previewIfdSize = 2 + 2 * 12 + 4;
        uint jpegOffset = (uint)(previewIfdOffset + previewIfdSize);

        tiff.WriteIfd((TagSubIfds, TypeLong, 1u, tiff.InlineU32((uint)previewIfdOffset)));
        tiff.WriteIfd(
            (TagStripOffsets, TypeLong, 1u, tiff.InlineU32(jpegOffset)),
            (TagStripByteCounts, TypeLong, 1u, tiff.InlineU32((uint)jpeg.Length)));
        tiff.WriteBytes(jpeg);

        byte[] raw = tiff.ToArray(ifd0Offset);
        Assert.Equal(0x4D, raw[0]); // 'M'
        Assert.Equal(0x4D, raw[1]); // 'M'
        Assert.Equal(0x00, raw[2]); // magic 42 的大端表示
        Assert.Equal(0x2A, raw[3]);

        byte[]? result = DngPreviewExtractor.TryExtractPreviewJpeg(new MemoryStream(raw));

        Assert.NotNull(result);
        Assert.Equal(jpeg, result);
    }

    // ---------- 用例 5a：数据不是合法 JPEG（缺 SOI / 缺 EOI） ----------
    [Fact]
    public void ReturnsNull_WhenPayloadIsNotJpeg()
    {
        byte[] noSoi = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0xFF, 0xD9];
        Assert.Null(DngPreviewExtractor.TryExtractPreviewJpeg(
            new MemoryStream(BuildSingleStripTiff(noSoi))));

        byte[] noEoi = [0xFF, 0xD8, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x00, 0x00];
        Assert.Null(DngPreviewExtractor.TryExtractPreviewJpeg(
            new MemoryStream(BuildSingleStripTiff(noEoi))));
    }

    // ---------- 用例 5b：offset 越界 ----------
    [Fact]
    public void ReturnsNull_WhenStripOffsetOutOfBounds()
    {
        var tiff = new TiffBuilder(littleEndian: true);
        tiff.WriteIfd(
            (TagStripOffsets, TypeLong, 1u, tiff.InlineU32(100_000)), // 远超文件长度
            (TagStripByteCounts, TypeLong, 1u, tiff.InlineU32(64)));
        byte[] data = tiff.ToArray(8);

        Assert.Null(DngPreviewExtractor.TryExtractPreviewJpeg(new MemoryStream(data)));
    }

    // ---------- 用例 5c：空流 / 短流 / 非 TIFF 内容 ----------
    [Fact]
    public void ReturnsNull_ForEmptyTruncatedOrGarbageStream()
    {
        Assert.Null(DngPreviewExtractor.TryExtractPreviewJpeg(
            new MemoryStream(Array.Empty<byte>())));
        Assert.Null(DngPreviewExtractor.TryExtractPreviewJpeg(
            new MemoryStream([0x49, 0x49]))); // 仅 "II"，不足 8 字节头
        Assert.Null(DngPreviewExtractor.TryExtractPreviewJpeg(
            new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]))); // PNG 头
    }

    // ---------- 用例 5d：预览超过 64MB 上限 ----------
    [Fact]
    public void ReturnsNull_WhenPreviewExceeds64MbLimit()
    {
        const uint claimedLength = 64u * 1024 * 1024 + 1; // 67,108,865
        var tiff = new TiffBuilder(littleEndian: true);

        const int ifd0Offset = 8;
        const int ifd0Size = 2 + 2 * 12 + 4;
        uint dataOffset = (uint)(ifd0Offset + ifd0Size);

        tiff.WriteIfd(
            (TagStripOffsets, TypeLong, 1u, tiff.InlineU32(dataOffset)),
            (TagStripByteCounts, TypeLong, 1u, tiff.InlineU32(claimedLength)));
        byte[] header = tiff.ToArray(ifd0Offset);

        // 真实提供声称的字节数且开头合法，确保命中“上限保护”而非“越界”
        var full = new byte[dataOffset + claimedLength];
        header.AsSpan().CopyTo(full);
        full[dataOffset] = 0xFF;
        full[dataOffset + 1] = 0xD8;

        byte[]? result = DngPreviewExtractor.TryExtractPreviewJpeg(new MemoryStream(full));

        Assert.Null(result);
    }

    // ---------- 用例 5e：BigTIFF(magic 43) ----------
    [Fact]
    public void ReturnsNull_ForBigTiff()
    {
        // BigTIFF 头 14 字节：字节序 + magic 43 + offsetSize(8) + 首个 IFD 偏移(8)
        byte[] littleEndianBigTiff =
        [
            0x49, 0x49, 0x2B, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        Assert.Null(DngPreviewExtractor.TryExtractPreviewJpeg(
            new MemoryStream(littleEndianBigTiff)));

        byte[] bigEndianBigTiff =
        [
            0x4D, 0x4D, 0x00, 0x2B, 0x00, 0x08, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10,
        ];
        Assert.Null(DngPreviewExtractor.TryExtractPreviewJpeg(
            new MemoryStream(bigEndianBigTiff)));
    }

    // ---------- 用例 6：不可 Seek 的流 ----------
    [Fact]
    public void ExtractsPreview_FromNonSeekableStream()
    {
        byte[] jpeg = MakeFakeJpeg(payloadBytes: 20);
        byte[] tiff = BuildSingleStripTiff(jpeg);

        using var stream = new ForwardOnlyStream(tiff);
        Assert.False(stream.CanSeek);

        byte[]? result = DngPreviewExtractor.TryExtractPreviewJpeg(stream);

        Assert.NotNull(result);
        Assert.Equal(jpeg, result);
    }

    // ---------- 补充：多组候选取字节数最大者（IFD0 小预览 vs SubIFD 大预览） ----------
    [Fact]
    public void PicksLargestPreview_AmongIfd0AndSubIfd()
    {
        byte[] smallJpeg = MakeFakeJpeg(payloadBytes: 6);  // 10 字节
        byte[] largeJpeg = MakeFakeJpeg(payloadBytes: 36); // 40 字节
        var tiff = new TiffBuilder(littleEndian: true);

        const int ifd0Offset = 8;
        const int ifd0Size = 2 + 3 * 12 + 4; // 0x111 + 0x117 + 0x14A 三个条目
        int previewIfdOffset = ifd0Offset + ifd0Size;
        const int previewIfdSize = 2 + 2 * 12 + 4;
        uint smallJpegOffset = (uint)(previewIfdOffset + previewIfdSize);
        uint largeJpegOffset = smallJpegOffset + (uint)smallJpeg.Length;

        tiff.WriteIfd(
            (TagStripOffsets, TypeLong, 1u, tiff.InlineU32(smallJpegOffset)),
            (TagStripByteCounts, TypeLong, 1u, tiff.InlineU32((uint)smallJpeg.Length)),
            (TagSubIfds, TypeLong, 1u, tiff.InlineU32((uint)previewIfdOffset)));
        tiff.WriteIfd(
            (TagStripOffsets, TypeLong, 1u, tiff.InlineU32(largeJpegOffset)),
            (TagStripByteCounts, TypeLong, 1u, tiff.InlineU32((uint)largeJpeg.Length)));
        tiff.WriteBytes(smallJpeg);
        tiff.WriteBytes(largeJpeg);

        byte[]? result = DngPreviewExtractor.TryExtractPreviewJpeg(new MemoryStream(tiff.ToArray(ifd0Offset)));

        Assert.NotNull(result);
        Assert.Equal(largeJpeg, result);
    }

    // ---------- 补充：只有 0x117 而无 0x111，不构成候选 ----------
    [Fact]
    public void ReturnsNull_WhenOnlyByteCountTagPresent()
    {
        var tiff = new TiffBuilder(littleEndian: true);
        tiff.WriteIfd((TagStripByteCounts, TypeLong, 1u, tiff.InlineU32(64)));

        Assert.Null(DngPreviewExtractor.TryExtractPreviewJpeg(new MemoryStream(tiff.ToArray(8))));
    }

    // ---------- 补充：可 Seek 但位置不在开头的流 ----------
    [Fact]
    public void ExtractsPreview_FromSeekableStreamNotAtStart()
    {
        byte[] jpeg = MakeFakeJpeg(payloadBytes: 12);
        byte[] tiff = BuildSingleStripTiff(jpeg);

        var stream = new MemoryStream(tiff);
        stream.Position = stream.Length; // 模拟复用过的流，位置在末尾

        byte[]? result = DngPreviewExtractor.TryExtractPreviewJpeg(stream);

        Assert.NotNull(result);
        Assert.Equal(jpeg, result);
    }

    // ---------- 补充：null 流 ----------
    [Fact]
    public void ReturnsNull_ForNullStream()
    {
        Assert.Null(DngPreviewExtractor.TryExtractPreviewJpeg(null!));
    }

    /// <summary>构造 SOI + 递增填充 + EOI 的假 JPEG，总长 = payloadBytes + 4（≥ 16 字节由调用方保证）。</summary>
    private static byte[] MakeFakeJpeg(int payloadBytes)
    {
        var jpeg = new byte[2 + payloadBytes + 2];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        for (int i = 0; i < payloadBytes; i++)
        {
            jpeg[2 + i] = (byte)(i + 1);
        }

        jpeg[^2] = 0xFF;
        jpeg[^1] = 0xD9;
        return jpeg;
    }

    /// <summary>构造“IFD0 直接携带单段 strip（LONG）”的最小 TIFF（小端），指向任意 payload。</summary>
    private static byte[] BuildSingleStripTiff(byte[] payload)
    {
        var tiff = new TiffBuilder(littleEndian: true);
        const int ifd0Offset = 8;
        const int ifd0Size = 2 + 2 * 12 + 4;
        uint payloadOffset = (uint)(ifd0Offset + ifd0Size);

        tiff.WriteIfd(
            (TagStripOffsets, TypeLong, 1u, tiff.InlineU32(payloadOffset)),
            (TagStripByteCounts, TypeLong, 1u, tiff.InlineU32((uint)payload.Length)));
        tiff.WriteBytes(payload);
        return tiff.ToArray(ifd0Offset);
    }

    /// <summary>
    /// 手工组装 TIFF 字节的辅助构建器：构造时预留 8 字节头，随后按写入顺序追加 IFD / 外部数组 / 数据，
    /// ToArray 时回填头部的字节序、magic 与 IFD0 偏移。写入方法返回数据起始偏移，便于交叉引用。
    /// </summary>
    private sealed class TiffBuilder
    {
        private readonly bool _littleEndian;
        private readonly MemoryStream _ms = new();

        public TiffBuilder(bool littleEndian)
        {
            _littleEndian = littleEndian;
            _ms.Write(new byte[8], 0, 8); // 头部占位，IFD0 固定从偏移 8 开始
        }

        public int WriteBytes(byte[] bytes)
        {
            int start = (int)_ms.Length;
            _ms.Write(bytes, 0, bytes.Length);
            return start;
        }

        public int WriteLongs(params uint[] values)
        {
            int start = (int)_ms.Length;
            foreach (uint value in values)
            {
                _ms.Write(U32Bytes(value));
            }

            return start;
        }

        /// <summary>写入一个 IFD（条目按 tag 升序，符合 TIFF 规范），nextIFD 置 0；返回 IFD 起始偏移。</summary>
        public int WriteIfd(params (ushort Tag, ushort Type, uint Count, byte[] ValueField)[] entries)
        {
            int start = (int)_ms.Length;
            _ms.Write(U16Bytes((ushort)entries.Length));
            foreach ((ushort tag, ushort type, uint count, byte[] valueField) in entries.OrderBy(e => e.Tag))
            {
                if (valueField.Length != 4)
                {
                    throw new ArgumentException("条目值字段必须是 4 字节。");
                }

                _ms.Write(U16Bytes(tag));
                _ms.Write(U16Bytes(type));
                _ms.Write(U32Bytes(count));
                _ms.Write(valueField);
            }

            _ms.Write(U32Bytes(0)); // next IFD
            return start;
        }

        /// <summary>内联 LONG 值（4 字节，count=1 时直接存于值字段）。</summary>
        public byte[] InlineU32(uint value) => U32Bytes(value);

        /// <summary>内联 SHORT 值（≤2 个，左对齐存于 4 字节值字段，余下补零）。</summary>
        public byte[] InlineShorts(params ushort[] values)
        {
            var field = new byte[4];
            int i = 0;
            foreach (ushort value in values)
            {
                byte[] bytes = U16Bytes(value);
                field[i++] = bytes[0];
                field[i++] = bytes[1];
            }

            return field;
        }

        public byte[] ToArray(uint ifd0Offset)
        {
            byte[] buffer = _ms.ToArray();
            buffer[0] = (byte)(_littleEndian ? 0x49 : 0x4D); // 'I' / 'M'
            buffer[1] = buffer[0];                            // 'I' / 'M'
            byte[] magic = U16Bytes(42);
            byte[] offset = U32Bytes(ifd0Offset);
            buffer[2] = magic[0];
            buffer[3] = magic[1];
            buffer[4] = offset[0];
            buffer[5] = offset[1];
            buffer[6] = offset[2];
            buffer[7] = offset[3];
            return buffer;
        }

        private byte[] U16Bytes(ushort value) => _littleEndian
            ? [(byte)value, (byte)(value >> 8)]
            : [(byte)(value >> 8), (byte)value];

        private byte[] U32Bytes(uint value) => _littleEndian
            ? [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)]
            : [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    /// <summary>只允许顺序 Read 的流包装：CanSeek=false，Seek/Position/Length 均抛异常。</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly byte[] _data = data;
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int readable = Math.Min(count, _data.Length - _position);
            if (readable <= 0)
            {
                return 0;
            }

            Array.Copy(_data, _position, buffer, offset, readable);
            _position += readable;
            return readable;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
