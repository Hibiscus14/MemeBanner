using System.Collections.Generic;
using UnityEngine;

internal static class GifDecoder
{
    private const byte BLOCK_EXTENSION = 0x21;
    private const byte BLOCK_IMAGE = 0x2C;
    private const byte BLOCK_TRAILER = 0x3B;
    private const byte EXTENSION_GRAPHIC_CONTROL = 0xF9;

    private const int COLOR_TABLE_FLAG = 0x80;
    private const int INTERLACE_FLAG = 0x40;

    private const int DISPOSE_BACKGROUND = 2;
    private const int DISPOSE_PREVIOUS = 3;

    private const int MAX_CODES = 4096;

    private enum BlockResult
    {
        Ok,
        Skip,
        Fatal
    }

    private struct GraphicControl
    {
        internal int DelayHundredths;
        internal int Disposal;
        internal int TransparentIndex;

        internal static GraphicControl Default()
        {
            return new GraphicControl { DelayHundredths = 10, Disposal = 0, TransparentIndex = -1 };
        }

        internal float Delay()
        {
            return DelayHundredths <= 1 ? 0.1f : DelayHundredths / 100f;
        }
    }

    private struct ImageBlock
    {
        internal int Left, Top, Width, Height;
        internal bool Interlaced;
        internal Color32[] Table;
        internal byte[] Indices;
    }

    internal static bool TryDecode(byte[] data, out Color32[][] frames, out float[] delays, out int width, out int height)
    {
        frames = null;
        delays = null;
        width = 0;
        height = 0;

        if (data.Length < 14 || data[0] != 'G' || data[1] != 'I' || data[2] != 'F')
        {
            return false;
        }

        int pos = 6;
        width = data[pos] | (data[pos + 1] << 8);
        height = data[pos + 2] | (data[pos + 3] << 8);
        int screenFlags = data[pos + 4];
        pos += 7;

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        Color32[] globalTable = null;
        if ((screenFlags & COLOR_TABLE_FLAG) != 0 &&
            !TryReadTable(data, ref pos, 2 << (screenFlags & 7), out globalTable))
        {
            return false;
        }

        List<Color32[]> frameList = new List<Color32[]>();
        List<float> delayList = new List<float>();
        Color32[] canvas = new Color32[width * height];
        Color32[] previous = null;
        GraphicControl control = GraphicControl.Default();

        while (pos < data.Length)
        {
            byte block = data[pos++];

            if (block == BLOCK_TRAILER)
            {
                break;
            }

            if (block == BLOCK_EXTENSION)
            {
                if (!ReadExtension(data, ref pos, ref control))
                {
                    break;
                }
                continue;
            }

            if (block != BLOCK_IMAGE)
            {
                break;
            }

            BlockResult result = ReadImageBlock(data, ref pos, globalTable, out ImageBlock image);
            if (result == BlockResult.Fatal)
            {
                break;
            }
            if (result == BlockResult.Skip)
            {
                continue;
            }

            if (control.Disposal == DISPOSE_PREVIOUS)
            {
                previous = (Color32[])canvas.Clone();
            }

            Composite(canvas, width, height, image, control.TransparentIndex);

            frameList.Add((Color32[])canvas.Clone());
            delayList.Add(control.Delay());

            if (control.Disposal == DISPOSE_BACKGROUND)
            {
                PixelCanvas.ClearRect(canvas, width, height,
                    image.Left, height - image.Top - image.Height, image.Width, image.Height);
            }
            else if (control.Disposal == DISPOSE_PREVIOUS && previous != null)
            {
                canvas = previous;
            }
        }

        if (frameList.Count == 0)
        {
            return false;
        }

        frames = frameList.ToArray();
        delays = delayList.ToArray();
        return true;
    }

    private static bool ReadExtension(byte[] data, ref int pos, ref GraphicControl control)
    {
        if (pos >= data.Length)
        {
            return false;
        }

        byte label = data[pos++];
        if (label == EXTENSION_GRAPHIC_CONTROL && pos + 5 <= data.Length)
        {
            int size = data[pos++];
            int flags = data[pos];
            control.DelayHundredths = data[pos + 1] | (data[pos + 2] << 8);
            control.TransparentIndex = (flags & 1) != 0 ? data[pos + 3] : -1;
            control.Disposal = (flags >> 2) & 7;
            pos += size;
        }
        SkipBlocks(data, ref pos);
        return true;
    }

    private static BlockResult ReadImageBlock(byte[] data, ref int pos, Color32[] globalTable, out ImageBlock image)
    {
        image = default;

        if (pos + 9 > data.Length)
        {
            return BlockResult.Fatal;
        }

        image.Left = data[pos] | (data[pos + 1] << 8);
        image.Top = data[pos + 2] | (data[pos + 3] << 8);
        image.Width = data[pos + 4] | (data[pos + 5] << 8);
        image.Height = data[pos + 6] | (data[pos + 7] << 8);
        int flags = data[pos + 8];
        pos += 9;

        Color32[] localTable = null;
        if ((flags & COLOR_TABLE_FLAG) != 0 && !TryReadTable(data, ref pos, 2 << (flags & 7), out localTable))
        {
            return BlockResult.Fatal;
        }
        image.Table = localTable ?? globalTable;
        image.Interlaced = (flags & INTERLACE_FLAG) != 0;

        if (pos >= data.Length)
        {
            return BlockResult.Fatal;
        }
        int minCodeSize = data[pos++];
        byte[] compressed = ReadBlocks(data, ref pos);

        if (image.Table == null || image.Width <= 0 || image.Height <= 0 || minCodeSize < 1 || minCodeSize > 11)
        {
            return BlockResult.Skip;
        }

        image.Indices = Decompress(compressed, minCodeSize, image.Width * image.Height);
        return BlockResult.Ok;
    }

    private static void Composite(Color32[] canvas, int width, int height, ImageBlock image, int transparentIndex)
    {
        for (int y = 0; y < image.Height; y++)
        {
            int row = image.Interlaced ? InterlacedRow(y, image.Height) : y;
            int canvasY = height - 1 - (image.Top + row);
            if (canvasY < 0 || canvasY >= height)
            {
                continue;
            }
            for (int x = 0; x < image.Width; x++)
            {
                int canvasX = image.Left + x;
                if (canvasX < 0 || canvasX >= width)
                {
                    continue;
                }
                int index = image.Indices[y * image.Width + x];
                if (index == transparentIndex || index >= image.Table.Length)
                {
                    continue;
                }
                canvas[canvasY * width + canvasX] = image.Table[index];
            }
        }
    }

    private static int InterlacedRow(int decodedRow, int height)
    {
        int pass1 = (height + 7) / 8;
        int pass2 = (height + 3) / 8;
        int pass3 = (height + 1) / 4;

        if (decodedRow < pass1)
        {
            return decodedRow * 8;
        }
        decodedRow -= pass1;
        if (decodedRow < pass2)
        {
            return decodedRow * 8 + 4;
        }
        decodedRow -= pass2;
        if (decodedRow < pass3)
        {
            return decodedRow * 4 + 2;
        }
        return (decodedRow - pass3) * 2 + 1;
    }

    private static bool TryReadTable(byte[] data, ref int pos, int count, out Color32[] table)
    {
        table = null;
        if (pos + count * 3 > data.Length)
        {
            return false;
        }
        table = new Color32[count];
        for (int i = 0; i < count; i++)
        {
            table[i] = new Color32(data[pos], data[pos + 1], data[pos + 2], 255);
            pos += 3;
        }
        return true;
    }

    private static void SkipBlocks(byte[] data, ref int pos)
    {
        while (pos < data.Length)
        {
            int length = data[pos++];
            if (length == 0)
            {
                return;
            }
            pos += length;
        }
    }

    private static byte[] ReadBlocks(byte[] data, ref int pos)
    {
        List<byte> buffer = new List<byte>();
        while (pos < data.Length)
        {
            int length = data[pos++];
            if (length == 0)
            {
                break;
            }
            for (int i = 0; i < length && pos < data.Length; i++)
            {
                buffer.Add(data[pos++]);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] Decompress(byte[] input, int minCodeSize, int pixelCount)
    {
        short[] prefix = new short[MAX_CODES];
        byte[] suffix = new byte[MAX_CODES];
        byte[] stack = new byte[MAX_CODES + 1];
        byte[] output = new byte[pixelCount];

        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int codeSize = minCodeSize + 1;
        int nextCode = endCode + 1;
        int previousCode = -1;
        int firstByte = 0;
        int bitPos = 0;
        int outPos = 0;
        int bitCount = input.Length * 8;

        for (int i = 0; i < clearCode; i++)
        {
            suffix[i] = (byte)i;
        }

        while (outPos < pixelCount && bitPos + codeSize <= bitCount)
        {
            int code = ReadBits(input, bitPos, codeSize);
            bitPos += codeSize;

            if (code == clearCode)
            {
                codeSize = minCodeSize + 1;
                nextCode = endCode + 1;
                previousCode = -1;
                continue;
            }
            if (code == endCode)
            {
                break;
            }

            int incoming = code;
            int stackPos = 0;
            if (code >= nextCode)
            {
                if (previousCode < 0)
                {
                    break;
                }
                stack[stackPos++] = (byte)firstByte;
                code = previousCode;
            }
            while (code >= clearCode && stackPos < MAX_CODES)
            {
                stack[stackPos++] = suffix[code];
                code = prefix[code];
            }
            firstByte = suffix[code];
            stack[stackPos++] = (byte)firstByte;

            while (stackPos > 0 && outPos < pixelCount)
            {
                output[outPos++] = stack[--stackPos];
            }

            if (previousCode >= 0 && nextCode < MAX_CODES)
            {
                prefix[nextCode] = (short)previousCode;
                suffix[nextCode] = (byte)firstByte;
                nextCode++;
                if (nextCode == (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }
            previousCode = incoming;
        }

        return output;
    }

    private static int ReadBits(byte[] data, int bitPos, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = bitPos + i;
            value |= ((data[bit >> 3] >> (bit & 7)) & 1) << i;
        }
        return value;
    }
}
