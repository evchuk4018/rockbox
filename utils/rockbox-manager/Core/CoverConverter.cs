using System.Buffers.Binary;

namespace RockboxManager.Core;

public sealed record RgbaImage(int Width, int Height, byte[] Pixels);

public static class CoverConverter
{
    public const int Size = 61, Inset = 0, Inner = Size;
    public const string Suffix = ".61x61.bmp", LegacySuffix = ".44x44.bmp";

    public static byte[] Placeholder()
    {
        var white = new bool[Size * Size];
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
        {
            var center = (Size - 1) / 2d;
            var distance = Math.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
            white[y * Size + x] = distance >= Size * .39 && distance <= Size * .43 ||
                distance >= Size * .14 && distance <= Size * .18 || distance <= Size * .045;
        }
        return Bitmap(white);
    }

    // Sansa Cover Studio defaults. Area sampling supplies the centered crop;
    // all subsequent operations follow the supplied HTML's default engine.
    public static byte[] Convert(RgbaImage image, int sourceInset = 0)
    {
        if (sourceInset < 0 || image.Width <= sourceInset * 2 || image.Height <= sourceInset * 2 ||
            image.Pixels.LongLength != (long)image.Width * image.Height * 4)
            throw new ArgumentException("Invalid RGBA image.");
        var luma = Crop(image, sourceInset);
        var histogram = new int[256];
        foreach (var value in luma) histogram[(int)value]++;
        int low = 0, high = 255, sum = 0;
        for (int i = 0; i < 256; i++) { sum += histogram[i]; if (sum >= luma.Length * .02) { low = i; break; } }
        sum = 0;
        for (int i = 255; i >= 0; i--) { sum += histogram[i]; if (sum >= luma.Length * .02) { high = i; break; } }
        if (high - low < 36) { low = 0; high = 255; }
        var mapped = luma.Select(v => Clamp(((v - low) * 255 / (high - low) - 127.5f) * 1.25f + 127.5f)).ToArray();
        var copy = (float[])mapped.Clone();
        for (int y = 1; y < Inner - 1; y++)
            for (int x = 1; x < Inner - 1; x++)
            {
                int i = y * Inner + x;
                mapped[i] = Clamp(copy[i] + (copy[i] - (copy[i - 1] + copy[i + 1] + copy[i - Inner] + copy[i + Inner]) / 4) * .4f);
            }
        var white = new bool[Size * Size];
        void Add(int x, int y, float error, float weight)
        { if (x >= 0 && x < Inner && y < Inner) mapped[y * Inner + x] += error * weight; }
        for (int y = 0; y < Inner; y++)
            for (int x = 0; x < Inner; x++)
            {
                int i = y * Inner + x; float old = mapped[i]; bool on = old >= 128;
                white[(y + Inset) * Size + x + Inset] = on;
                float error = old - (on ? 255 : 0);
                Add(x + 1, y, error, 7f / 16); Add(x - 1, y + 1, error, 3f / 16);
                Add(x, y + 1, error, 5f / 16); Add(x + 1, y + 1, error, 1f / 16);
            }
        return Bitmap(white);
    }

    private static float Clamp(float value) => Math.Clamp(value, 0, 255);
    private static float[] Crop(RgbaImage image, int sourceInset)
    {
        double side = Math.Min(image.Width, image.Height) - 2 * sourceInset, step = side / Inner;
        double left = (image.Width - side) / 2, top = (image.Height - side) / 2;
        var luma = new float[Inner * Inner];
        for (int y = 0; y < Inner; y++)
            for (int x = 0; x < Inner; x++)
            {
                double x0 = left + x * step, x1 = x0 + step, y0 = top + y * step, y1 = y0 + step;
                double r = 0, g = 0, b = 0;
                for (int sy = (int)y0; sy < Math.Ceiling(y1) && sy < image.Height; sy++)
                    for (int sx = (int)x0; sx < Math.Ceiling(x1) && sx < image.Width; sx++)
                    {
                        double weight = (Math.Min(x1, sx + 1) - Math.Max(x0, sx)) *
                            (Math.Min(y1, sy + 1) - Math.Max(y0, sy)) / (step * step);
                        int p = (sy * image.Width + sx) * 4; double alpha = image.Pixels[p + 3] / 255d;
                        r += image.Pixels[p] * alpha * weight;
                        g += image.Pixels[p + 1] * alpha * weight; b += image.Pixels[p + 2] * alpha * weight;
                    }
                luma[y * Inner + x] = (float)Math.Clamp(Math.Floor(.2126 * r + .7152 * g + .0722 * b + .5), 0, 255);
            }
        return luma;
    }

    public static byte[] Bitmap(bool[] white, int size = Size)
    {
        if (white.Length != size * size) throw new ArgumentException("Wrong bitmap dimensions.");
        int stride = (size + 31) / 32 * 4;
        var bmp = new byte[62 + stride * size]; bmp[0] = 66; bmp[1] = 77;
        void I32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(offset), value);
        I32(2, bmp.Length); I32(10, 62); I32(14, 40); I32(18, size); I32(22, size);
        bmp[26] = 1; bmp[28] = 1; I32(34, stride * size);
        I32(38, 2835); I32(42, 2835); I32(46, 2); I32(50, 2);
        bmp[58] = bmp[59] = bmp[60] = 255;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            if (white[y * size + x]) bmp[62 + (size - 1 - y) * stride + x / 8] |= (byte)(1 << (7 - x % 8));
        return bmp;
    }

    public static bool IsCover(byte[] bytes, int size = Size) => bytes.Length == 62 + (size + 31) / 32 * 4 * size && bytes[0] == 66 && bytes[1] == 77 &&
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10)) == 62 &&
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18)) == size &&
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22)) == size &&
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26)) == 1 &&
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)) == 1 &&
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(30)) == 0;
}
