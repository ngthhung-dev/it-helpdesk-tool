using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace ITHelpdeskTool.Services.Fonts;

/// <summary>Kết quả phân tích nhanh một file font đã cài.</summary>
public sealed record FontScanResult(string Family, bool Vietnamese, bool Legacy);

/// <summary>
/// Đọc nhanh tên họ font (bảng name) và kiểm tra đủ dấu tiếng Việt (bảng cmap) bằng cách tự đọc file OpenType —
/// nhanh hơn GlyphTypeface rất nhiều (không dựng toàn bộ bảng ký tự), chạy song song được, có bộ nhớ đệm trên đĩa.
/// </summary>
public static class FontScan
{
    private const string VnChars = "ăâđêôơưĂÂĐÊÔƠƯàảãáạằẳẵắặầẩẫấậèẻẽéẹềểễếệìỉĩíịòỏõóọồổỗốộờởỡớợùủũúụừửữứựỳỷỹýỵ";

    public static string CacheFile
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ITHelpdeskTool");
            Directory.CreateDirectory(d);
            return Path.Combine(d, "fontscan.tsv");
        }
    }

    /// <summary>Font mã cũ (TCVN3 ".VnTime", VNI "VNI-Times", VPS…) — gõ được tiếng Việt nhưng chỉ với bảng mã riêng.</summary>
    public static bool IsLegacyName(string family) =>
        family.StartsWith(".Vn", StringComparison.OrdinalIgnoreCase) || family.StartsWith("VNI-", StringComparison.OrdinalIgnoreCase) ||
        family.StartsWith("VNI ", StringComparison.OrdinalIgnoreCase) || family.StartsWith("VN-", StringComparison.OrdinalIgnoreCase) ||
        family.StartsWith("VPS ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Phân tích nhiều file song song, dùng lại kết quả cũ nếu file không đổi.</summary>
    public static Dictionary<string, FontScanResult> ScanMany(IReadOnlyList<InstalledFont> fonts, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var cache = LoadCache();
        var result = new ConcurrentDictionary<string, FontScanResult>(StringComparer.OrdinalIgnoreCase);
        var fresh = new ConcurrentDictionary<string, (string Key, FontScanResult R)>(StringComparer.OrdinalIgnoreCase);
        var files = fonts.Select(f => f.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8), CancellationToken = ct }, file =>
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Exists)
                {
                    var key = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
                    if (cache.TryGetValue(file, out var c) && c.Key == key) result[file] = c.R;
                    else
                    {
                        var r = Probe(file);
                        result[file] = r;
                        fresh[file] = (key, r);
                    }
                }
            }
            catch { }
            var n = Interlocked.Increment(ref done);
            if (n % 200 == 0 || n == files.Count) progress?.Invoke(n, files.Count);
        });
        if (!fresh.IsEmpty)
        {
            foreach (var kv in fresh) cache[kv.Key] = kv.Value;
            SaveCache(cache, files);
        }
        return new Dictionary<string, FontScanResult>(result, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, (string Key, FontScanResult R)> LoadCache()
    {
        var d = new Dictionary<string, (string, FontScanResult)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(CacheFile)) return d;
            foreach (var line in File.ReadLines(CacheFile, Encoding.UTF8))
            {
                var p = line.Split('\t');
                if (p.Length != 5) continue;
                d[p[0]] = (p[1], new FontScanResult(p[2], p[3] == "1", p[4] == "1"));
            }
        }
        catch { }
        return d;
    }

    private static void SaveCache(Dictionary<string, (string Key, FontScanResult R)> cache, List<string> keep)
    {
        try
        {
            var set = keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (var kv in cache.Where(kv => set.Contains(kv.Key)))
                sb.Append(kv.Key).Append('\t').Append(kv.Value.Key).Append('\t').Append(kv.Value.R.Family.Replace('\t', ' '))
                  .Append('\t').Append(kv.Value.R.Vietnamese ? '1' : '0').Append('\t').Append(kv.Value.R.Legacy ? '1' : '0').Append('\n');
            var tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            File.Move(tmp, CacheFile, true);
        }
        catch { }
    }

    /// <summary>Đọc tên họ và kiểm tra dấu tiếng Việt của một file font.</summary>
    public static FontScanResult Probe(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".fon" or ".fnt") return new FontScanResult(stem, false, IsLegacyName(stem));
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            var head = ReadAt(fs, 0, 16);
            long fontOff = 0;
            if (head.Length >= 16 && head[0] == 't' && head[1] == 't' && head[2] == 'c' && head[3] == 'f')
                fontOff = BinaryPrimitives.ReadUInt32BigEndian(ReadAt(fs, 12, 4));
            var hdr = ReadAt(fs, fontOff, 12);
            if (hdr.Length < 12) return new FontScanResult(stem, false, false);
            int numTables = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(4));
            if (numTables is 0 or > 200) return new FontScanResult(stem, false, false);
            var dir = ReadAt(fs, fontOff + 12, numTables * 16);
            (long Off, int Len) cmap = default, name = default;
            for (var i = 0; i + 16 <= dir.Length; i += 16)
            {
                var tag = Encoding.ASCII.GetString(dir, i, 4);
                var off = BinaryPrimitives.ReadUInt32BigEndian(dir.AsSpan(i + 8));
                var len = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(dir.AsSpan(i + 12)), 16 * 1024 * 1024);
                if (tag == "cmap") cmap = (off, len);
                else if (tag == "name") name = (off, len);
            }
            var family = name.Len > 0 ? FamilyName(ReadAt(fs, name.Off, name.Len)) ?? stem : stem;
            var vn = cmap.Len > 0 && HasAll(ReadAt(fs, cmap.Off, cmap.Len), VnChars);
            return new FontScanResult(family, vn, !vn && IsLegacyName(family));
        }
        catch { return new FontScanResult(stem, false, IsLegacyName(stem)); }
    }

    private static byte[] ReadAt(FileStream fs, long off, int len)
    {
        if (off < 0 || off >= fs.Length) return [];
        len = (int)Math.Min(len, fs.Length - off);
        var b = new byte[len];
        fs.Position = off;
        fs.ReadExactly(b);
        return b;
    }

    private static ushort U16(byte[] b, int o) => o + 2 <= b.Length ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)) : (ushort)0;
    private static uint U32(byte[] b, int o) => o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o)) : 0;

    /// <summary>Tên họ (nameID 1) — giống tên Win32 Windows dùng, ưu tiên tiếng Anh.</summary>
    internal static string? FamilyName(byte[] t)
    {
        int count = U16(t, 2), strOff = U16(t, 4);
        string? best = null;
        var bestScore = -1;
        for (var i = 0; i < count; i++)
        {
            var r = 6 + i * 12;
            if (r + 12 > t.Length) break;
            int plat = U16(t, r), enc = U16(t, r + 2), lang = U16(t, r + 4), id = U16(t, r + 6), len = U16(t, r + 8), off = U16(t, r + 10);
            if (id != 1) continue;
            var score = plat == 3 && lang == 0x409 ? 3 : plat == 3 ? 2 : plat == 1 && enc == 0 ? 1 : -1;
            if (score <= bestScore) continue;
            var s = strOff + off;
            if (s + len > t.Length || len == 0) continue;
            var text = plat == 3 ? Encoding.BigEndianUnicode.GetString(t, s, len) : Encoding.Latin1.GetString(t, s, len);
            text = text.Trim('\0', ' ');
            if (text.Length == 0) continue;
            best = text;
            bestScore = score;
        }
        return best;
    }

    /// <summary>Bảng cmap có đủ mọi ký tự trong chuỗi không (đọc subtable format 4 / 12).</summary>
    internal static bool HasAll(byte[] t, string chars)
    {
        int n = U16(t, 2);
        int best = -1, bestScore = -1;
        for (var i = 0; i < n; i++)
        {
            var r = 4 + i * 8;
            int plat = U16(t, r), enc = U16(t, r + 2);
            var off = (int)U32(t, r + 4);
            if (off <= 0 || off >= t.Length) continue;
            int fmt = U16(t, off);
            var score = (plat, enc, fmt) switch
            {
                (3, 10, 12) => 5,
                (0, _, 12) => 4,
                (3, 1, 4) => 3,
                (0, _, 4) => 2,
                _ => -1,
            };
            if (score > bestScore) { bestScore = score; best = off; }
        }
        if (best < 0) return false;
        return U16(t, best) == 12 ? chars.All(c => Has12(t, best, c)) : chars.All(c => Has4(t, best, c));
    }

    private static bool Has4(byte[] t, int o, char c)
    {
        int segX2 = U16(t, o + 6);
        int ends = o + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
        for (var s = 0; s < segX2; s += 2)
        {
            int end = U16(t, ends + s);
            if (end < c) continue;
            int start = U16(t, starts + s);
            if (start > c) return false;
            int delta = U16(t, deltas + s), ro = U16(t, ranges + s);
            if (ro == 0) return ((c + delta) & 0xFFFF) != 0;
            int g = U16(t, ranges + s + ro + 2 * (c - start));
            return g != 0 && ((g + delta) & 0xFFFF) != 0;
        }
        return false;
    }

    private static bool Has12(byte[] t, int o, char c)
    {
        var groups = U32(t, o + 12);
        for (long i = 0; i < groups; i++)
        {
            var g = o + 16 + (int)i * 12;
            if (g + 12 > t.Length) return false;
            if (c >= U32(t, g) && c <= U32(t, g + 4)) return true;
        }
        return false;
    }
}
