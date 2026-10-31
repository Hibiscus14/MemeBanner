using System.Collections.Generic;
using System.Text;
using UnityEngine;

internal static class ApngDecoder
{
    private const byte DISPOSE_BACKGROUND = 1;
    private const byte DISPOSE_PREVIOUS = 2;
    private const byte BLEND_OVER = 1;

    private const int FCTL_LENGTH = 26;
    private const int CHUNK_OVERHEAD = 12;

    private sealed class FrameInfo
    {
        internal int Width, Height, X, Y;
        internal float Delay;
        internal byte Dispose, Blend;
        internal readonly List<byte[]> Parts = new List<byte[]>();
    }

    internal static bool TryDecode(byte[] data, out Color32[][] frames, out float[] delays, out int width, out int height)
    {
        frames = null;
        delays = null;
        width = 0;
        height = 0;

        if (data.Length < 8 || data[0] != 0x89 || data[1] != 'P' || data[2] != 'N' || data[3] != 'G')
        {
            return false;
        }

        byte[] header = null;
        List<byte[]> carried = new List<byte[]>();
        List<FrameInfo> infos = new List<FrameInfo>();
        bool animated = false;
        int pos = 8;

        while (pos + 8 <= data.Length)
        {
            int length = ReadInt(data, pos);
            if (length < 0 || pos + CHUNK_OVERHEAD + length > data.Length)
            {
                break;
            }
            string type = Encoding.ASCII.GetString(data, pos + 4, 4);
            int body = pos + 8;

            switch (type)
            {
                case "IHDR":
                    header = Slice(data, body, length);
                    width = ReadInt(data, body);
                    height = ReadInt(data, body + 4);
                    break;

                case "acTL":
                    animated = true;
                    break;

                case "fcTL":
                    if (length >= FCTL_LENGTH)
                    {
                        int den = (data[body + 22] << 8) | data[body + 23];
                        infos.Add(new FrameInfo
                        {
                            Width = ReadInt(data, body + 4),
                            Height = ReadInt(data, body + 8),
                            X = ReadInt(data, body + 12),
                            Y = ReadInt(data, body + 16),
                            Delay = ((data[body + 20] << 8) | data[body + 21]) / (den == 0 ? 100f : den),
                            Dispose = data[body + 24],
                            Blend = data[body + 25]
                        });
                    }
                    break;

                case "IDAT":


                    if (infos.Count == 1)
                    {
                        infos[0].Parts.Add(Slice(data, body, length));
                    }
                    break;

                case "fdAT":
                    if (infos.Count > 0 && length > 4)
                    {
                        infos[infos.Count - 1].Parts.Add(Slice(data, body + 4, length - 4));
                    }
                    break;

                case "PLTE":
                case "tRNS":
                case "gAMA":
                case "cHRM":
                case "sRGB":
                case "iCCP":
                case "sBIT":
                case "pHYs":
                    carried.Add(Slice(data, pos, length + CHUNK_OVERHEAD));
                    break;
            }

            pos += CHUNK_OVERHEAD + length;
            if (type == "IEND")
            {
                break;
            }
        }

        if (!animated || header == null || width <= 0 || height <= 0 || infos.Count == 0)
        {
            return false;
        }

        List<Color32[]> frameList = new List<Color32[]>();
        List<float> delayList = new List<float>();
        Color32[] canvas = new Color32[width * height];
        Color32[] previous = null;
        Texture2D scratch = new Texture2D(2, 2, TextureFormat.RGBA32, false);

        foreach (FrameInfo info in infos)
        {
            if (info.Parts.Count == 0 || info.Width <= 0 || info.Height <= 0)
            {
                continue;
            }
            if (!scratch.LoadImage(BuildPng(header, carried, info)))
            {
                continue;
            }
            Color32[] pixels = scratch.GetPixels32();
            if (pixels.Length < info.Width * info.Height)
            {
                continue;
            }

            if (info.Dispose == DISPOSE_PREVIOUS)
            {
                previous = (Color32[])canvas.Clone();
            }

            int bottom = height - info.Y - info.Height;
            for (int y = 0; y < info.Height; y++)
            {
                int canvasY = bottom + y;
                if (canvasY < 0 || canvasY >= height)
                {
                    continue;
                }
                for (int x = 0; x < info.Width; x++)
                {
                    int canvasX = info.X + x;
                    if (canvasX < 0 || canvasX >= width)
                    {
                        continue;
                    }
                    Color32 source = pixels[y * info.Width + x];
                    int target = canvasY * width + canvasX;
                    canvas[target] = info.Blend == BLEND_OVER ? Over(source, canvas[target]) : source;
                }
            }

            frameList.Add((Color32[])canvas.Clone());
            delayList.Add(Mathf.Max(info.Delay, 0.01f));

            if (info.Dispose == DISPOSE_BACKGROUND)
            {
                PixelCanvas.ClearRect(canvas, width, height, info.X, bottom, info.Width, info.Height);
            }
            else if (info.Dispose == DISPOSE_PREVIOUS && previous != null)
            {
                canvas = previous;
            }
        }

        Object.Destroy(scratch);

        if (frameList.Count == 0)
        {
            return false;
        }

        frames = frameList.ToArray();
        delays = delayList.ToArray();
        return true;
    }

    private static Color32 Over(Color32 source, Color32 under)
    {
        if (source.a == 255 || under.a == 0)
        {
            return source;
        }
        if (source.a == 0)
        {
            return under;
        }
        float sa = source.a / 255f;
        float ua = under.a / 255f;
        float outA = sa + ua * (1f - sa);
        if (outA <= 0f)
        {
            return new Color32(0, 0, 0, 0);
        }
        return new Color32(
            Mix(source.r, under.r, sa, ua, outA),
            Mix(source.g, under.g, sa, ua, outA),
            Mix(source.b, under.b, sa, ua, outA),
            (byte)Mathf.RoundToInt(outA * 255f));
    }

    private static byte Mix(byte source, byte under, float sa, float ua, float outA)
    {
        float value = (source / 255f * sa + under / 255f * ua * (1f - sa)) / outA;
        return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
    }
    

    private static byte[] BuildPng(byte[] header, List<byte[]> carried, FrameInfo info)
    {
        byte[] ihdr = (byte[])header.Clone();
        WriteInt(ihdr, 0, info.Width);
        WriteInt(ihdr, 4, info.Height);

        int total = 0;
        foreach (byte[] part in info.Parts)
        {
            total += part.Length;
        }
        byte[] image = new byte[total];
        int offset = 0;
        foreach (byte[] part in info.Parts)
        {
            System.Buffer.BlockCopy(part, 0, image, offset, part.Length);
            offset += part.Length;
        }

        List<byte> png = new List<byte>(total + 256);
        png.AddRange(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        AppendChunk(png, "IHDR", ihdr);
        foreach (byte[] chunk in carried)
        {
            png.AddRange(chunk);
        }
        AppendChunk(png, "IDAT", image);
        AppendChunk(png, "IEND", new byte[0]);
        return png.ToArray();
    }

    private static void AppendChunk(List<byte> png, string type, byte[] body)
    {
        byte[] length = new byte[4];
        WriteInt(length, 0, body.Length);
        png.AddRange(length);

        byte[] typed = new byte[4 + body.Length];
        for (int i = 0; i < 4; i++)
        {
            typed[i] = (byte)type[i];
        }
        System.Buffer.BlockCopy(body, 0, typed, 4, body.Length);
        png.AddRange(typed);

        byte[] crc = new byte[4];
        WriteInt(crc, 0, unchecked((int)Crc32(typed)));
        png.AddRange(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in bytes)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }

    private static byte[] Slice(byte[] data, int offset, int length)
    {
        byte[] result = new byte[length];
        System.Buffer.BlockCopy(data, offset, result, 0, length);
        return result;
    }

    private static int ReadInt(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }

    private static void WriteInt(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
