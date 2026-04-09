using SkiaSharp;
using CoreJ2K.Skia;
using CoreJ2K.Configuration;

namespace OpenSim.Framework;

public static class SkiaImageUtils
{
    private static readonly J2KEncoderConfiguration encoderConfiguration = new J2KEncoderConfiguration().WithLossless().WithFileFormat(true);

    public static bool TryEncodeToJ2K(SKBitmap inputImage, out byte[] encoded)
    {
        encoded = null;

        if (inputImage is null) return false;

        using var normalized = NormalizeColorType(inputImage);
        encoded = normalized.EncodeToJ2K(encoderConfiguration);

        return encoded is not null && encoded.Length != 0;
    }

    public static bool TryEncodeToPng(SKBitmap inputImage, out byte[] encoded)
    {
        encoded = null;

        if (inputImage is null) return false;

        using var normalized = NormalizeColorType(inputImage);
        using var data = normalized.Encode(SKEncodedImageFormat.Png, 100);
        encoded = data?.ToArray();

        return encoded is not null && encoded.Length != 0;
    }

    public static bool TryEncodeToJpeg(SKBitmap inputImage, int quality, out byte[] encoded)
    {
        encoded = null;

        if (inputImage is null) return false;

        using var normalized = NormalizeColorType(inputImage);
        using var data = normalized.Encode(SKEncodedImageFormat.Jpeg, quality);
        encoded = data?.ToArray();

        return encoded is not null && encoded.Length != 0;
    }

    public static bool TryDecodeFromJ2K(byte[] inData, out SKBitmap decoded)
    {
        decoded = null;

        if (inData is null || inData.Length == 0) return false;

        using var inputImage = SKBitmapJ2kExtensions.FromJ2KBytes(inData);
        if (inputImage is null) return false;

        decoded = NormalizeColorType(inputImage);

        return true;
    }

    public static bool TryDecodeFromBytes(byte[] inData, out SKBitmap decoded)
    {
        decoded = null;

        if (inData is null || inData.Length == 0) return false;

        using var inputImage = SKBitmap.Decode(inData);
        if (inputImage is null) return false;

        decoded = NormalizeColorType(inputImage);

        return true;
    }




    public static SKBitmap NormalizeColorType(SKBitmap input)
    {
        if (input is null) return null;
        return input.Copy(SKColorType.Bgra8888);
    }

    public static bool IsJpeg(byte[] checkMe)
    {
        if (checkMe is null || checkMe.Length < 3) return false;

        return checkMe[0] == 0xFF && checkMe[1] == 0xD8 && checkMe[2] == 0xFF;
    }

    // ************************************************************

    // Old method to be replaced.
    public static SKBitmap ResizeImageSolid(SKBitmap image, int width, int height)
    {
        SKBitmap result = new(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque);

        using (SKCanvas canvas = new(result))
        using (SKPaint paint = new())
        {
            paint.IsAntialias = true;
            paint.FilterQuality = SKFilterQuality.High;

            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(image, new SKRect(0, 0, width, height), paint);
        }

        return result;
    }
}
