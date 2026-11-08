using System.Buffers.Binary;

namespace RockboxManager.Core;

public sealed record RgbaImage(int Width, int Height, byte[] Pixels);

public static class CoverConverter
{
    public const int Size = 44, Inset = 2, Inner = Size - 2 * Inset;

    public static byte[] Placeholder()
    {
        var white = new bool[Size * Size];
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
        {
            var distance = (x - 21.5) * (x - 21.5) + (y - 21.5) * (y - 21.5);
            white[y * Size + x] = distance is >= 289 and <= 361 or >= 36 and <= 64 or <= 4;
        }
        return Bitmap(white);
    }

    // Sansa Cover Studio defaults. Area sampling supplies the centered crop;
    // all subsequent operations follow the supplied HTML's default engine.
    public static byte[] Convert(RgbaImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.LongLength != (long)image.Width * image.Height * 4)
            throw new ArgumentException("Invalid RGBA image.");
        var luma = Crop(image);
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
    private static float[] Crop(RgbaImage image)
    {
        double side = Math.Min(image.Width, image.Height), step = side / Inner;
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

    public static byte[] Bitmap(bool[] white)
    {
        if (white.Length != Size * Size) throw new ArgumentException("Wrong bitmap dimensions.");
        const int stride = (Size + 31) / 32 * 4;
        var bmp = new byte[62 + stride * Size]; bmp[0] = 66; bmp[1] = 77;
        void I32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(offset), value);
        I32(2, bmp.Length); I32(10, 62); I32(14, 40); I32(18, Size); I32(22, Size);
        bmp[26] = 1; bmp[28] = 1; I32(34, stride * Size);
        I32(38, 2835); I32(42, 2835); I32(46, 2); I32(50, 2);
        bmp[58] = bmp[59] = bmp[60] = 255;
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
            if (white[y * Size + x]) bmp[62 + (Size - 1 - y) * stride + x / 8] |= (byte)(1 << (7 - x % 8));
        return bmp;
    }

    public static bool IsCover(byte[] bytes) => bytes.Length == 414 && bytes[0] == 66 && bytes[1] == 77 &&
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10)) == 62 &&
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18)) == Size &&
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22)) == Size &&
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26)) == 1 &&
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)) == 1 &&
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(30)) == 0;
}
