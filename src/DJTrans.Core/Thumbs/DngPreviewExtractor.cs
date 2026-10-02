namespace DJTrans.Core.Thumbs;

/// <summary>
/// 从 DNG/TIFF 字节流中尽力提取内嵌预览 JPEG 的原始字节（仅搬运内嵌 JPEG，不做完整 RAW 解码）。
/// </summary>
public static class DngPreviewExtractor
{
    /// <summary>预览总字节数上限：超过视为元数据异常，放弃该候选。</summary>
    private const int MaxPreviewBytes = 64 * 1024 * 1024;

    /// <summary>数组型标签（StripOffsets 等）允许的最大元素个数，防御异常元数据。</summary>
    private const int MaxArrayValues = 1 << 20;

    private const int TiffHeaderSize = 8;
    private const int IfdEntrySize = 12;
    private const ushort TiffMagic = 42; // 标准 TIFF/DNG；43 为 BigTIFF，直接放弃

    private const ushort TagStripOffsets = 0x111;    // StripOffsets / DNG: PreviewImageStart
    private const ushort TagStripByteCounts = 0x117; // StripByteCounts / DNG: PreviewImageLength
    private const ushort TagSubIfds = 0x14A;         // SubIFDs

    private readonly record struct IfdEntry(ushort Type, uint Count, int ValueFieldOffset);

    /// <summary>
    /// 尝试从 DNG/TIFF 字节流中提取内嵌预览 JPEG 的原始字节。失败返回 null（调用方回退其他缩略图策略）。
    /// </summary>
    /// <remarks>
    /// 候选来源：IFD0 自身的 (0x111, 0x117) 标签对，以及 IFD0 的 SubIFDs(0x14A) 一级子 IFD 中的同款标签对。
    /// 候选按总字节数从大到小依次尝试，返回第一组能完整拷贝且以 SOI(FFD8) 开头、EOI(FFD9) 结尾的数据
    /// （即字节数最大的合法预览）；多段 strip 按数组顺序拼接。
    /// 可 Seek 的流会先回到开头再读取；不可 Seek 的流从当前位置整体读入内存。
    /// 本 API 尽力而为：任何解析异常都返回 null。
    /// </remarks>
    public static byte[]? TryExtractPreviewJpeg(Stream stream)
    {
        try
        {
            if (stream is null)
            {
                return null;
            }

            byte[] data = ReadAllBytes(stream);
            return Extract(data);
        }
        catch
        {
            return null; // 契约即“尽力而为”：任何异常一律视为提取失败
        }
    }

    /// <summary>把流当前位置之后的内容完整读入内存（不可 Seek 的流无法随机访问，必须先整体复制）。</summary>
    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.CanSeek && stream.Position != 0)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[]? Extract(byte[] data)
    {
        if (data.Length < TiffHeaderSize)
        {
            return null;
        }

        bool littleEndian = data[0] == 0x49 && data[1] == 0x49; // "II"
        if (!littleEndian && !(data[0] == 0x4D && data[1] == 0x4D)) // 也不是 "MM"
        {
            return null;
        }

        if (ReadUInt16(data, 2, littleEndian) != TiffMagic)
        {
            return null; // BigTIFF(43) 或其他变体不支持
        }

        long ifd0Offset = ReadUInt32(data, 4, littleEndian);
        Dictionary<ushort, IfdEntry>? ifd0 = ReadIfd(data, ifd0Offset, littleEndian);
        if (ifd0 is null)
        {
            return null;
        }

        var candidates = new List<(long[] Offsets, long[] Counts)>();

        // 候选一：IFD0 自身的 strip 对
        AddStripPairIfPresent(data, ifd0, littleEndian, candidates);

        // 候选二：SubIFDs 指向的一级子 IFD 中的 strip 对（DNG 全尺寸预览常在这里）
        long[]? subIfdOffsets = ReadTagValues(data, ifd0, TagSubIfds, littleEndian);
        if (subIfdOffsets is not null)
        {
            foreach (long subIfdOffset in subIfdOffsets)
            {
                Dictionary<ushort, IfdEntry>? subIfd = ReadIfd(data, subIfdOffset, littleEndian);
                if (subIfd is not null)
                {
                    AddStripPairIfPresent(data, subIfd, littleEndian, candidates);
                }
            }
        }

        // 过滤异常候选（空数据或超过 64MB 上限），再按总字节数降序尝试
        var valid = new List<(long[] Offsets, long[] Counts, long Total)>();
        foreach ((long[] offsets, long[] counts) in candidates)
        {
            long total = 0;
            foreach (long count in counts)
            {
                total += count;
            }

            if (total <= 0 || total > MaxPreviewBytes)
            {
                continue;
            }

            valid.Add((offsets, counts, total));
        }

        foreach ((long[] offsets, long[] counts, long total) in valid.OrderByDescending(v => v.Total))
        {
            byte[]? preview = CopySegmentsAndValidate(data, offsets, counts, total);
            if (preview is not null)
            {
                return preview;
            }
        }

        return null;
    }

    /// <summary>若 IFD 同时含 StripOffsets 与 StripByteCounts，则登记为一组候选。</summary>
    private static void AddStripPairIfPresent(
        byte[] data,
        Dictionary<ushort, IfdEntry> ifd,
        bool littleEndian,
        List<(long[] Offsets, long[] Counts)> candidates)
    {
        long[]? offsets = ReadTagValues(data, ifd, TagStripOffsets, littleEndian);
        long[]? counts = ReadTagValues(data, ifd, TagStripByteCounts, littleEndian);
        if (offsets is null || counts is null || offsets.Length == 0 || counts.Length == 0)
        {
            return;
        }

        candidates.Add((offsets, counts));
    }

    /// <summary>按数组顺序拷贝各段并校验 JPEG 边界标记；任一段越界或校验失败则该候选无效。</summary>
    private static byte[]? CopySegmentsAndValidate(byte[] data, long[] offsets, long[] counts, long totalBytes)
    {
        var preview = new byte[(int)totalBytes];
        int copied = 0;
        int segments = Math.Min(offsets.Length, counts.Length);
        for (int i = 0; i < segments; i++)
        {
            long offset = offsets[i];
            long count = counts[i];
            if (count == 0)
            {
                continue;
            }

            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                return null; // 越界视为元数据损坏
            }

            Buffer.BlockCopy(data, (int)offset, preview, copied, (int)count);
            copied += (int)count;
        }

        if (copied < 4)
        {
            return null;
        }

        if (preview[0] != 0xFF || preview[1] != 0xD8 || preview[copied - 2] != 0xFF || preview[copied - 1] != 0xD9)
        {
            return null; // 不是合法 JPEG（缺 SOI/EOI）
        }

        return copied == preview.Length ? preview : preview[..copied];
    }

    /// <summary>解析一个 IFD 的标签表；偏移越界或长度不足时返回 null。</summary>
    private static Dictionary<ushort, IfdEntry>? ReadIfd(byte[] data, long offset, bool littleEndian)
    {
        if (offset < 0 || offset + 2 > data.Length)
        {
            return null;
        }

        int entryCount = ReadUInt16(data, (int)offset, littleEndian);
        long entriesEnd = offset + 2 + (long)entryCount * IfdEntrySize;
        if (entriesEnd > data.Length)
        {
            return null;
        }

        var entries = new Dictionary<ushort, IfdEntry>(entryCount);
        int position = (int)offset + 2;
        for (int i = 0; i < entryCount; i++)
        {
            ushort tag = ReadUInt16(data, position, littleEndian);
            entries[tag] = new IfdEntry(
                Type: ReadUInt16(data, position + 2, littleEndian),
                Count: ReadUInt32(data, position + 4, littleEndian),
                ValueFieldOffset: position + 8);
            position += IfdEntrySize;
        }

        return entries;
    }

    /// <summary>
    /// 读取数值型标签（BYTE/SHORT/LONG，单个或数组）。值不超过 4 字节时内联在条目值字段中，
    /// 否则值字段存的是外部数据偏移。类型不支持、数量异常或越界时返回 null（视同标签不存在）。
    /// </summary>
    private static long[]? ReadTagValues(byte[] data, Dictionary<ushort, IfdEntry> ifd, ushort tag, bool littleEndian)
    {
        if (!ifd.TryGetValue(tag, out IfdEntry entry))
        {
            return null;
        }

        int elementSize = entry.Type switch
        {
            1 => 1, // BYTE
            3 => 2, // SHORT
            4 => 4, // LONG
            _ => 0, // 其余类型（RATIONAL/LONG8 等）不支持
        };

        if (elementSize == 0 || entry.Count == 0 || entry.Count > MaxArrayValues)
        {
            return null;
        }

        int count = (int)entry.Count;
        long byteLength = (long)count * elementSize;
        long valueOffset;
        if (byteLength <= 4)
        {
            valueOffset = entry.ValueFieldOffset; // 值内联在条目的 4 字节字段中
        }
        else
        {
            valueOffset = ReadUInt32(data, entry.ValueFieldOffset, littleEndian); // 字段存外部数据偏移
        }

        if (valueOffset < 0 || valueOffset + byteLength > data.Length)
        {
            return null;
        }

        var values = new long[count];
        int position = (int)valueOffset;
        for (int i = 0; i < count; i++)
        {
            values[i] = elementSize switch
            {
                1 => data[position],
                2 => ReadUInt16(data, position, littleEndian),
                _ => ReadUInt32(data, position, littleEndian),
            };
            position += elementSize;
        }

        return values;
    }

    private static ushort ReadUInt16(byte[] data, int position, bool littleEndian)
        => littleEndian
            ? (ushort)(data[position] | (data[position + 1] << 8))
            : (ushort)((data[position] << 8) | data[position + 1]);

    private static uint ReadUInt32(byte[] data, int position, bool littleEndian)
        => littleEndian
            ? (uint)(data[position] | (data[position + 1] << 8) | (data[position + 2] << 16) | (data[position + 3] << 24))
            : (uint)((data[position] << 24) | (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3]);
}
