using System.Windows.Media;
using System.Windows.Media.Imaging;
using RockboxManager.Core;

namespace RockboxManager;

internal static class ArtworkDecoder
{
    public static RgbaImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000) throw new NotSupportedException("Cover image dimensions are too large.");
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(frame.PixelWidth * frame.PixelHeight * 4)];
        converted.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return new(frame.PixelWidth, frame.PixelHeight, pixels);
    }

    public static BitmapSource Preview(byte[] bmp)
    {
        using var stream = new MemoryStream(bmp, writable: false);
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        image.Freeze(); return image;
    }
}
