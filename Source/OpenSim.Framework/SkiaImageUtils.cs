/*
 * Copyright (C) 2026 Iain McCracken
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 *
 */

using SkiaSharp;
using CoreJ2K.Skia;
using CoreJ2K.Configuration;

namespace OpenSim.Framework;

public static class SkiaImageUtils
{
    /// <summary>
    /// A basic lossless JPEG2000 encoder configuration
    /// </summary>
    private static readonly J2KEncoderConfiguration encoderConfiguration = new J2KEncoderConfiguration().WithLossless().WithFileFormat(true);

    /// <summary>
    /// Create a fresh empty opaque SKBitmap with the appropriate pixel format
    /// </summary>
    /// <param name="xsize"></param>
    /// <param name="ysize"></param>
    /// <returns></returns>
    public static SKBitmap NewDefaultSKBitmap(int xsize, int ysize)
    {
        return new SKBitmap(xsize, ysize, SKColorType.Bgra8888, SKAlphaType.Opaque);
    }


    /// <summary>
    /// Try to encode a bitmap to JPEG2000, lossless.
    /// </summary>
    /// <param name="inputImage">a Skia bitmap</param>
    /// <param name="encoded">(output) JPEG2000 bytes</param>
    /// <returns>true if the encode succeeded</returns>
    public static bool TryEncodeToJ2K(SKBitmap inputImage, out byte[] encoded)
    {
        encoded = null;

        // Bypass the exception throwing null check in SkiaSharp
        if (inputImage is null) return false;

        using SKBitmap normalized = NormalizeColorType(inputImage);
        encoded = normalized.EncodeToJ2K(encoderConfiguration);

        return encoded is not null && encoded.Length != 0;
    }

    /// <summary>
    /// Try to encode a bitmap to PNG.
    /// </summary>
    /// <param name="inputImage">a Skia bitmap</param>
    /// <param name="encoded">(output) PNG bytes</param>
    /// <returns>true if the encoding succeeded.</returns>
    public static bool TryEncodeToPng(SKBitmap inputImage, out byte[] encoded)
    {
        encoded = null;

        // Bypass the exception throwing null check in SkiaSharp
        if (inputImage is null) return false;

        using SKBitmap normalized = NormalizeColorType(inputImage);
        using SKData data = normalized.Encode(SKEncodedImageFormat.Png, 100);
        encoded = data?.ToArray();

        return encoded is not null && encoded.Length != 0;
    }

    /// <summary>
    /// Try to encode a bitmap to JPEG
    /// </summary>
    /// <param name="inputImage">a Skia bitmap</param>
    /// <param name="quality">encoding quality</param>
    /// <param name="encoded">(output) JPEG bytes</param>
    /// <returns>true if the encoding succeeded</returns>
    public static bool TryEncodeToJpeg(SKBitmap inputImage, int quality, out byte[] encoded)
    {
        encoded = null;

        // Bypass the exception throwing null check in SkiaSharp
        if (inputImage is null) return false;

        using SKBitmap normalized = NormalizeColorType(inputImage);
        using SKData data = normalized.Encode(SKEncodedImageFormat.Jpeg, quality);
        encoded = data?.ToArray();

        return encoded is not null && encoded.Length != 0;
    }

    /// <summary>
    /// Try to decode a JPEG2000
    /// </summary>
    /// <param name="inData">bytes of a JPEG2000 image</param>
    /// <param name="decoded">(output) a Skia bitmap with 32-bit bgra pixel format</param>
    /// <returns>true if the decode succeeded</returns>
    public static bool TryDecodeFromJ2K(byte[] inData, out SKBitmap decoded)
    {
        decoded = null;

        if (inData is null || inData.Length == 0) return false;

        using SKBitmap inputImage = SKBitmapJ2kExtensions.FromJ2KBytes(inData);
        if (inputImage is null) return false;

        decoded = NormalizeColorType(inputImage);

        return true;
    }

    /// <summary>
    /// Try to decode an image (other than a JPEG2000)
    /// </summary>
    /// <param name="inData">bytes of an image</param>
    /// <param name="decoded">(output) a Skia bitmap with 32-bit bgra pixel format</param>
    /// <returns>true if the decode succeeded</returns>
    public static bool TryDecodeFromBytes(byte[] inData, out SKBitmap decoded)
    {
        decoded = null;

        if (inData is null || inData.Length == 0) return false;

        using SKBitmap inputImage = SKBitmap.Decode(inData);
        if (inputImage is null) return false;

        decoded = NormalizeColorType(inputImage);

        return true;
    }

    public static SKBitmap OpaqueResize(SKBitmap input, int x, int y)
    {
        if (input is null) return null;
        return input.Resize(new SKImageInfo(x,y,SKColorType.Bgra8888,SKAlphaType.Opaque),new SKSamplingOptions(SKFilterMode.Linear,SKMipmapMode.None));
    }

    /// <summary>Normalize a bitmap to the bgra8888 pixel format.</summary>
    /// <remarks>
    /// <para>SkiaSharp doesn't always play nice with encoding and decoding. The 32-bit bgra pixel format does play nice with
    /// encoding both JPEG and JPEG2000. It is also the pixel format provided by the Warp3D library.</para>
    /// 
    /// <para><b>Note: Always returns a new SKBitmap! You now own both!</b></para>
    /// </remarks>
    /// <param name="input">An input bbitmap</param>
    /// <returns>A normalized bitmap</returns>
    public static SKBitmap NormalizeColorType(SKBitmap input)
    {
        // Bypass the null-check in SKBitmap.Copy which throws an exception.
        if (input is null) return null;
        return input.Copy(SKColorType.Bgra8888);
    }

    /// <summary>
    /// Check if a file is *not* a JPEG
    /// </summary>
    /// <param name="checkMe">At least the first 3 bytes of the file</param>
    /// <returns>true if the file is definitely not a JPEG</returns>
    public static bool IsNotJpeg(byte[] checkMe)
    {
        if (checkMe is null || checkMe.Length < 3) return true;

        return checkMe[0] != 0xFF || checkMe[1] != 0xD8 || checkMe[2] != 0xFF;
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
