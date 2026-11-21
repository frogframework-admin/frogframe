using System.Text;

namespace Framework.Web.Identity;

/// <summary>
/// Minimal QR Code encoder (ISO/IEC 18004): byte mode, error correction level M, versions 1–10 (up to 213 bytes).
/// Used to show the authenticator-app enrolment link (otpauth://…) as a scannable image without a third-party
/// package or sending the secret to an external QR service. Output is an inline SVG.
/// </summary>
public static class QrCode
{
    private const int MaxVersion = 10;
    // Level M, versions 1..10: error correction codewords per block, and number of blocks.
    private static readonly int[] EccPerBlock = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
    private static readonly int[] NumBlocks = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

    /// <summary>Module matrix (true = dark), without the quiet zone. Throws when the text does not fit version 10-M.</summary>
    public static bool[,] Encode(string text, int? forceMask = null)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var version = 1;
        for (; version <= MaxVersion; version++)
            if (4 + CountBits(version) + data.Length * 8 <= DataCodewords(version) * 8) break;
        if (version > MaxVersion) throw new ArgumentException($"Text too long for a QR code ({data.Length} bytes; max 213).", nameof(text));

        // Data bits: mode (byte = 0100), character count, bytes, terminator, padding.
        var bits = new List<bool>();
        void Append(int value, int length) { for (var i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Append(0b0100, 4);
        Append(data.Length, CountBits(version));
        foreach (var b in data) Append(b, 8);
        var capacity = DataCodewords(version) * 8;
        Append(0, Math.Min(4, capacity - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);
        var codewords = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(0x80 >> (i & 7));

        var all = AddEccAndInterleave(codewords, version);
        var size = version * 4 + 17;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];
        DrawFunctionPatterns(modules, isFunction, version);
        DrawCodewords(modules, isFunction, all);

        var best = forceMask ?? Enumerable.Range(0, 8).MinBy(mask =>
        {
            var trial = (bool[,])modules.Clone();
            ApplyMask(trial, isFunction, mask);
            DrawFormatBits(trial, isFunction, mask);
            return Penalty(trial);
        });
        ApplyMask(modules, isFunction, best);
        DrawFormatBits(modules, isFunction, best);
        return modules;
    }

    /// <summary>SVG image of the code with a 4-module quiet zone. Colours are fixed (dark on white) so scanners read it in dark mode too.</summary>
    public static string ToSvg(string text, int pixelSize = 200)
    {
        var m = Encode(text);
        var size = m.GetLength(0);
        const int quiet = 4;
        var total = size + quiet * 2;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (m[y, x]) path.Append('M').Append(x + quiet).Append(',').Append(y + quiet).Append("h1v1h-1z");
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {total} {total}\" width=\"{pixelSize}\" height=\"{pixelSize}\" " +
               $"shape-rendering=\"crispEdges\" role=\"img\" aria-label=\"QR code\"><rect width=\"{total}\" height=\"{total}\" fill=\"#fff\"/>" +
               $"<path d=\"{path}\" fill=\"#000\"/></svg>";
    }

    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    private static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var numAlign = version / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version) => RawDataModules(version) / 8 - EccPerBlock[version] * NumBlocks[version];

    private static byte[] AddEccAndInterleave(byte[] data, int version)
    {
        int numBlocks = NumBlocks[version], eccLen = EccPerBlock[version], raw = RawDataModules(version) / 8;
        int numShort = numBlocks - raw % numBlocks, shortLen = raw / numBlocks;
        var divisor = ReedSolomonDivisor(eccLen);
        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            var datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
            var dat = data.AsSpan(k, datLen).ToArray();
            k += datLen;
            var ecc = ReedSolomonRemainder(dat, divisor);
            var block = new byte[shortLen + 1];
            dat.CopyTo(block, 0);
            // Short blocks get a placeholder byte at the end of their data part, skipped when interleaving.
            ecc.CopyTo(block, shortLen + 1 - eccLen);
            blocks.Add(block);
        }
        var result = new List<byte>(raw);
        for (var i = 0; i < shortLen + 1; i++)
            for (var j = 0; j < blocks.Count; j++)
                if (i != shortLen - eccLen || j >= numShort) result.Add(blocks[j][i]);
        return result.ToArray();
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private static void DrawFunctionPatterns(bool[,] m, bool[,] f, int version)
    {
        var size = m.GetLength(0);
        void Set(int x, int y, bool dark) { m[y, x] = dark; f[y, x] = true; }
        for (var i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }

        foreach (var (cx, cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
            for (var dy = -4; dy <= 4; dy++)
                for (var dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy, dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (x >= 0 && x < size && y >= 0 && y < size) Set(x, y, dist != 2 && dist != 4);
                }

        var align = AlignmentPositions(version, size);
        for (var i = 0; i < align.Length; i++)
            for (var j = 0; j < align.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == align.Length - 1) || (i == align.Length - 1 && j == 0)) continue;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                        Set(align[i] + dx, align[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }

        DrawFormatBits(m, f, 0); // reserves the format areas; redrawn with the real mask later

        if (version >= 7)
        {
            var rem = version;
            for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            var bits = (version << 12) | rem;
            for (var i = 0; i < 18; i++)
            {
                var dark = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(a, b, dark);
                Set(b, a, dark);
            }
        }
    }

    private static int[] AlignmentPositions(int version, int size)
    {
        if (version == 1) return [];
        var numAlign = version / 7 + 2;
        var step = (version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private static void DrawFormatBits(bool[,] m, bool[,] f, int mask)
    {
        var size = m.GetLength(0);
        var data = (0b00 << 3) | mask; // level M = 00
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = ((data << 10) | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        void Set(int x, int y, bool dark) { m[y, x] = dark; f[y, x] = true; }

        for (var i = 0; i <= 5; i++) Set(8, i, Bit(i));
        Set(8, 7, Bit(6));
        Set(8, 8, Bit(7));
        Set(7, 8, Bit(8));
        for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));
        for (var i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(i));
        Set(8, size - 8, true); // dark module
    }

    private static void DrawCodewords(bool[,] m, bool[,] f, byte[] data)
    {
        var size = m.GetLength(0);
        var i = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vert : vert;
                    if (!f[y, x] && i < data.Length * 8)
                    {
                        m[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
        }
    }

    private static void ApplyMask(bool[,] m, bool[,] f, int mask)
    {
        var size = m.GetLength(0);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                if (f[y, x]) continue;
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) m[y, x] = !m[y, x];
            }
    }

    /// <summary>Mask penalty (runs, 2×2 blocks, finder-like patterns, dark balance), lower is easier to scan.</summary>
    private static int Penalty(bool[,] m)
    {
        var size = m.GetLength(0);
        var penalty = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < size; a++)
            {
                var run = 1;
                var line = new bool[size];
                for (var b = 0; b < size; b++) line[b] = pass == 0 ? m[a, b] : m[b, a];
                for (var b = 1; b < size; b++)
                {
                    if (line[b] == line[b - 1]) { run++; if (run == 5) penalty += 3; else if (run > 5) penalty++; }
                    else run = 1;
                }
                for (var b = 0; b + 7 <= size; b++)
                {
                    var finder = line[b] && !line[b + 1] && line[b + 2] && line[b + 3] && line[b + 4] && !line[b + 5] && line[b + 6];
                    if (!finder) continue;
                    var lightBefore = b >= 4 && !line[b - 1] && !line[b - 2] && !line[b - 3] && !line[b - 4];
                    var lightAfter = b + 11 <= size && !line[b + 7] && !line[b + 8] && !line[b + 9] && !line[b + 10];
                    if (lightBefore || lightAfter) penalty += 40;
                }
            }
        var dark = 0;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                if (m[y, x]) dark++;
                if (x + 1 < size && y + 1 < size && m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) penalty += 3;
            }
        var total = size * size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return penalty + Math.Max(0, k) * 10;
    }
}
