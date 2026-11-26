using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmartClipVault.Services;

/// <summary>
/// Lightweight pure-C# QR Code generator (Byte mode, ECC-L, Versions 1..10) with zero external dependencies.
/// </summary>
public static class QrCodeService
{
    private static readonly int[] TotalCodewords = [0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346];
    private static readonly int[] EcCodewordsPerBlock = [0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18];
    private static readonly int[] NumBlocks = [0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4];
    private static readonly int[][] AlignmentPositions =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34],
        [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50]
    ];

    public static BitmapSource? Generate(string text, int pixelsPerModule = 6)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var dataBytes = Encoding.UTF8.GetBytes(text);
        var version = 0;
        for (var v = 1; v <= 10; v++)
        {
            var dataCap = TotalCodewords[v] - EcCodewordsPerBlock[v] * NumBlocks[v];
            var headerBits = v <= 9 ? 12 : 20;
            if (dataBytes.Length * 8 + headerBits <= dataCap * 8)
            {
                version = v;
                break;
            }
        }

        if (version == 0)
            return null;

        var size = version * 4 + 17;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];

        DrawFunctionPatterns(version, size, modules, isFunction);
        var allCodewords = EncodeDataAndEcc(version, dataBytes);
        DrawCodewords(size, modules, isFunction, allCodewords);

        // Apply mask 0: (r + c) % 2 == 0
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
                if (!isFunction[r, c] && ((r + c) & 1) == 0)
                    modules[r, c] = !modules[r, c];

        DrawFormatBits(size, modules, 0);

        const int quiet = 2;
        var imgSize = (size + quiet * 2) * pixelsPerModule;
        var pixels = new byte[imgSize * imgSize];
        Array.Fill(pixels, (byte)255);

        for (var r = 0; r < size; r++)
        {
            for (var c = 0; c < size; c++)
            {
                if (!modules[r, c]) continue;
                var y0 = (r + quiet) * pixelsPerModule;
                var x0 = (c + quiet) * pixelsPerModule;
                for (var dy = 0; dy < pixelsPerModule; dy++)
                    for (var dx = 0; dx < pixelsPerModule; dx++)
                        pixels[(y0 + dy) * imgSize + (x0 + dx)] = 0;
            }
        }

        var bmp = BitmapSource.Create(imgSize, imgSize, 96, 96, PixelFormats.Gray8, null, pixels, imgSize);
        bmp.Freeze();
        return bmp;
    }

    private static void DrawFunctionPatterns(int version, int size, bool[,] modules, bool[,] isFunc)
    {
        void SetFunc(int r, int c, bool dark)
        {
            if (r >= 0 && r < size && c >= 0 && c < size)
            {
                modules[r, c] = dark;
                isFunc[r, c] = true;
            }
        }

        void DrawFinder(int r0, int c0)
        {
            for (var dr = -1; dr <= 7; dr++)
                for (var dc = -1; dc <= 7; dc++)
                {
                    var dist = Math.Max(Math.Abs(dr - 3), Math.Abs(dc - 3));
                    SetFunc(r0 + dr, c0 + dc, dist != 2 && dist != 4);
                }
        }

        DrawFinder(0, 0);
        DrawFinder(size - 7, 0);
        DrawFinder(0, size - 7);

        for (var i = 8; i < size - 8; i++)
        {
            SetFunc(6, i, i % 2 == 0);
            SetFunc(i, 6, i % 2 == 0);
        }

        var align = AlignmentPositions[version];
        foreach (var r in align)
            foreach (var c in align)
                if (!isFunc[r, c])
                    for (var dr = -2; dr <= 2; dr++)
                        for (var dc = -2; dc <= 2; dc++)
                            SetFunc(r + dr, c + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);

        SetFunc(size - 8, 8, true);
        for (var i = 0; i < 9; i++)
        {
            if (!isFunc[8, i]) SetFunc(8, i, false);
            if (!isFunc[i, 8]) SetFunc(i, 8, false);
        }
        for (var i = 0; i < 8; i++)
        {
            SetFunc(8, size - 1 - i, false);
            SetFunc(size - 1 - i, 8, false);
        }
    }

    private static void DrawFormatBits(int size, bool[,] modules, int mask)
    {
        // ECC L (01) + mask 0 (000) -> 01000, BCH(15,5) + XOR 101010000010010 = 0x77C4
        const int bits = 0x77C4;
        for (var i = 0; i <= 5; i++) modules[8, i] = ((bits >> i) & 1) != 0;
        modules[8, 7] = ((bits >> 6) & 1) != 0;
        modules[8, 8] = ((bits >> 7) & 1) != 0;
        modules[7, 8] = ((bits >> 8) & 1) != 0;
        for (var i = 9; i < 15; i++) modules[14 - i, 8] = ((bits >> i) & 1) != 0;

        for (var i = 0; i < 8; i++) modules[8, size - 1 - i] = ((bits >> i) & 1) != 0;
        for (var i = 8; i < 15; i++) modules[size - 15 + i, 8] = ((bits >> i) & 1) != 0;
    }

    private static byte[] EncodeDataAndEcc(int version, byte[] dataBytes)
    {
        var totalCw = TotalCodewords[version];
        var ecLen = EcCodewordsPerBlock[version];
        var blocks = NumBlocks[version];
        var dataCap = totalCw - ecLen * blocks;

        var bits = new List<bool>(dataCap * 8);
        void AppendBits(int val, int len)
        {
            for (var i = len - 1; i >= 0; i--)
                bits.Add(((val >> i) & 1) != 0);
        }

        AppendBits(0b0100, 4); // Byte mode
        AppendBits(dataBytes.Length, version <= 9 ? 8 : 16);
        foreach (var b in dataBytes) AppendBits(b, 8);
        AppendBits(0, Math.Min(4, dataCap * 8 - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(false);

        var dataCw = new byte[dataCap];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) dataCw[i / 8] |= (byte)(1 << (7 - (i % 8)));

        for (var i = bits.Count / 8; i < dataCap; i++)
            dataCw[i] = (byte)((i - bits.Count / 8) % 2 == 0 ? 0xEC : 0x11);

        var gen = ReedSolomonGenerator(ecLen);
        var shortBlockLen = dataCap / blocks;
        var numLongBlocks = dataCap % blocks;
        var dataBlocks = new byte[blocks][];
        var eccBlocks = new byte[blocks][];
        var offset = 0;

        for (var b = 0; b < blocks; b++)
        {
            var len = shortBlockLen + (b >= blocks - numLongBlocks ? 1 : 0);
            dataBlocks[b] = dataCw[offset..(offset + len)];
            eccBlocks[b] = ReedSolomonCompute(dataBlocks[b], gen);
            offset += len;
        }

        var result = new List<byte>(totalCw);
        var maxDataLen = shortBlockLen + (numLongBlocks > 0 ? 1 : 0);
        for (var i = 0; i < maxDataLen; i++)
            for (var b = 0; b < blocks; b++)
                if (i < dataBlocks[b].Length) result.Add(dataBlocks[b][i]);

        for (var i = 0; i < ecLen; i++)
            for (var b = 0; b < blocks; b++)
                result.Add(eccBlocks[b][i]);

        return [.. result];
    }

    private static byte[] ReedSolomonGenerator(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = GfMul(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = GfMul(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonCompute(byte[] data, byte[] gen)
    {
        var result = new byte[gen.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < gen.Length; i++)
                result[i] ^= GfMul(gen[i], factor);
        }
        return result;
    }

    private static byte GfMul(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private static void DrawCodewords(int size, bool[,] modules, bool[,] isFunc, byte[] data)
    {
        var i = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vert : vert;
                    if (!isFunc[y, x] && i < data.Length * 8)
                    {
                        modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
            }
        }
    }
}
