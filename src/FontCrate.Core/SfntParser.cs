using System.Buffers.Binary;
using System.Text;

namespace FontCrate.Core;

/// <summary>Bounded SFNT metadata reader. Windows adds DirectWrite validation before accepting results.</summary>
public sealed class SfntParser : IFontParser
{
    public Task<IReadOnlyList<ParsedFace>> ParseAsync(string path, CancellationToken ct) =>
        Task.Run<IReadOnlyList<ParsedFace>>(() => Parse(path, ct), ct);

    private static IReadOnlyList<ParsedFace> Parse(string path, CancellationToken ct)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = Read(f, 0, 12);
        var magic = U32(header, 0);
        uint[] offsets;
        if (magic == 0x74746366)
        {
            int count = checked((int)U32(header, 8));
            if (count is < 1 or > 4096) throw new InvalidDataException("字体集合 face 数量异常");
            var data = Read(f, 12, checked(count * 4));
            offsets = Enumerable.Range(0, count).Select(i => U32(data, i * 4)).ToArray();
        }
        else offsets = [0];
        var result = new List<ParsedFace>();
        for (int index = 0; index < offsets.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            header = Read(f, offsets[index], 12);
            if (U32(header, 0) is not (0x00010000 or 0x4F54544F or 0x74727565))
                throw new InvalidDataException("不是支持的 OpenType/TrueType 字体");
            int tableCount = U16(header, 4);
            if (tableCount is < 1 or > 4096) throw new InvalidDataException("字体表数量异常");
            var directory = Read(f, offsets[index] + 12L, tableCount * 16);
            var tables = new Dictionary<string, (long Offset, int Length)>();
            for (int i = 0; i < tableCount; i++)
            {
                int p = i * 16;
                string tag = Encoding.ASCII.GetString(directory, p, 4);
                long offset = U32(directory, p + 8);
                int length = checked((int)U32(directory, p + 12));
                if (offset + length > f.Length)
                    throw new InvalidDataException($"字体表越界：{tag}");
                tables[tag] = (offset, length);
            }
            byte[] Table(string tag, bool required = false)
            {
                if (tables.TryGetValue(tag, out var t))
                {
                    if (t.Length > 64 * 1024 * 1024) throw new InvalidDataException($"元数据表过大：{tag}");
                    return Read(f, t.Offset, t.Length);
                }
                if (required) throw new InvalidDataException($"缺少字体表：{tag}");
                return [];
            }
            var names = Names(Table("name", true));
            string Name(int id, string fallback = "") => names.GetValueOrDefault(id, fallback);
            string family = Name(16, Name(1));
            string style = Name(17, Name(2, "Regular"));
            if (string.IsNullOrWhiteSpace(family)) throw new InvalidDataException("缺少字体家族名称");
            var os2 = Table("OS/2");
            int weight = os2.Length >= 8 ? U16(os2, 4) : 400;
            int width = os2.Length >= 8 ? U16(os2, 6) : 5;
            bool italic = os2.Length >= 64 && (U16(os2, 62) & 1) != 0;
            var maxp = Table("maxp", true);
            int glyphs = U16(maxp, 4);
            var coverage = Coverage(Table("cmap", true), glyphs, ct);
            result.Add(new(index, family, style, Name(4, family + " " + style), Name(6), Name(5),
                weight, width, italic, Table("fvar").Length >= 16, coverage));
        }
        return result;
    }

    private static Dictionary<int, string> Names(byte[] b)
    {
        int count = U16(b, 2), start = U16(b, 4);
        var values = new Dictionary<int, (int Score, string Text)>();
        for (int i = 0; i < count; i++)
        {
            int p = 6 + i * 12;
            int platform = U16(b, p), language = U16(b, p + 4), id = U16(b, p + 6);
            int size = U16(b, p + 8), offset = checked(start + U16(b, p + 10));
            if (offset + size > b.Length) throw new InvalidDataException("name 表越界");
            if (platform is not (0 or 3)) continue;
            string text = Encoding.BigEndianUnicode.GetString(b, offset, size).Trim('\0', ' ');
            int score = (language == 0x0804 ? 30 : language == 0x0409 ? 20 : 0) + (platform == 3 ? 2 : 1);
            if (text.Length > 0 && (!values.TryGetValue(id, out var old) || old.Score < score)) values[id] = (score, text);
        }
        return values.ToDictionary(p => p.Key, p => p.Value.Text);
    }

    private static IReadOnlyList<UnicodeRange> Coverage(byte[] cmap, int glyphs, CancellationToken ct)
    {
        int count = U16(cmap, 2);
        var ranges = new List<UnicodeRange>();
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int p = 4 + i * 8;
            int platform = U16(cmap, p), encoding = U16(cmap, p + 2);
            int offset = checked((int)U32(cmap, p + 4));
            if (!(platform == 0 || platform == 3 && encoding is 1 or 10) || !seen.Add(offset)) continue;
            int format = U16(cmap, offset);
            if (format == 12)
            {
                int len = checked((int)U32(cmap, offset + 4));
                if (len < 16 || offset + (long)len > cmap.Length) throw new InvalidDataException("cmap12 越界");
                var b = cmap.AsSpan(offset, len).ToArray();
                int n = checked((int)U32(b, 12));
                if (n > 100000 || 16L + n * 12L > len) throw new InvalidDataException("cmap12 组数量异常");
                for (int j = 0; j < n; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    int q = 16 + j * 12;
                    long first = U32(b, q), last = U32(b, q + 4), glyph = U32(b, q + 8);
                    if (first > last || last > 0x10FFFF) throw new InvalidDataException("Unicode 范围异常");
                    last = Math.Min(last, first + glyphs - 1L - glyph);
                    if (glyph == 0) first++;
                    if (first <= last) ranges.Add(new((int)first, (int)last));
                }
            }
            else if (format == 4)
            {
                int len = U16(cmap, offset + 2);
                if (offset + len > cmap.Length || len < 16) throw new InvalidDataException("cmap4 越界");
                var b = cmap.AsSpan(offset, len).ToArray();
                int n = U16(b, 6) / 2;
                if (n < 1 || 16L + n * 8L > len) throw new InvalidDataException("cmap4 段数量异常");
                for (int j = 0; j < n; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    int end = U16(b, 14 + j * 2), start = U16(b, 16 + n * 2 + j * 2);
                    int delta = U16(b, 16 + n * 4 + j * 2), rp = 16 + n * 6 + j * 2, ro = U16(b, rp);
                    if (start > end) throw new InvalidDataException("cmap4 段范围异常");
                    int run = -1;
                    for (int c = start; c <= end && c < 65535; c++)
                    {
                        int glyph = ro == 0 ? (c + delta) & 65535 : U16(b, checked(rp + ro + (c - start) * 2));
                        if (ro != 0 && glyph != 0) glyph = (glyph + delta) & 65535;
                        bool valid = glyph > 0 && glyph < glyphs && c is not (>= 0xD800 and <= 0xDFFF);
                        if (valid && run < 0) run = c;
                        if ((!valid || c == end || c == 65534) && run >= 0)
                        { ranges.Add(new(run, valid ? c : c - 1)); run = -1; }
                    }
                }
            }
        }
        var merged = new List<UnicodeRange>();
        foreach (var r in ranges.OrderBy(r => r.First))
        {
            if (merged.Count > 0 && r.First <= merged[^1].Last + 1)
                merged[^1] = merged[^1] with { Last = Math.Max(merged[^1].Last, r.Last) };
            else merged.Add(r);
        }
        return merged;
    }

    private static byte[] Read(FileStream f, long offset, int size)
    {
        if (offset < 0 || size < 0 || offset + size > f.Length) throw new InvalidDataException("字体文件截断");
        var data = new byte[size]; f.Position = offset; f.ReadExactly(data); return data;
    }
    private static ushort U16(byte[] b, int p)
    {
        if (p < 0 || p + 2L > b.Length) throw new InvalidDataException("字体字段越界");
        return BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p, 2));
    }
    private static uint U32(byte[] b, int p)
    {
        if (p < 0 || p + 4L > b.Length) throw new InvalidDataException("字体字段越界");
        return BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4));
    }
}
