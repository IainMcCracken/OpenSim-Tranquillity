CoreJ2K.Skia decodes JPEG2000 files into SKColorType.Rgba8888 and SKAlphaType.Unpremul

Can output to SKBitmap or SKImage with the image. The main code outputs a SKBitmap, but a SKImage will work in the InterleavedImage

Warp3DImageModule -- does fake J2000 stuff -- should be fixed.

SkiaSharp can save:
JPEG -- any quality level
PNG
WEBP -- only 100% quality level (not particularly useful) at other quality levels, Microsoft's image handling code barfs in Paint.NET trying to load the file.


