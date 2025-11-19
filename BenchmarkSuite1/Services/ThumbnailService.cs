using System;
using System.IO;
using System.Threading.Tasks;
using SkiaSharp;

namespace BenchmarkSuite1.Services;

public class ThumbnailService
{
    private const int ThumbnailHeight = 200;
    private const int ThumbnailQuality = 60;

    public async Task<string?> CreateThumbnailAsync(string sourceImagePath, string thumbnailPath)
    {
        try
        {
            if (File.Exists(thumbnailPath))
                return thumbnailPath;

            await using var sourceStream = File.OpenRead(sourceImagePath);
            using var codec = SKCodec.Create(sourceStream);

            if (codec == null)
                return null;

            var origin = codec.EncodedOrigin;
            var originalWidth = codec.Info.Width;
            var originalHeight = codec.Info.Height;
            
            if (origin.IsRotate90or270())
            {
                (originalWidth, originalHeight) = (originalHeight, originalWidth);
            }

            var (targetWidth, targetHeight) = CalculateThumbnailSize(originalWidth, originalHeight);
            var sampleSize = CalculateSampleSize(codec.Info.Width, codec.Info.Height, targetWidth, targetHeight);
            
            var bitmap = SKBitmap.Decode(codec, new SKImageInfo(
                codec.Info.Width / sampleSize,
                codec.Info.Height / sampleSize,
                SKColorType.Rgba8888
            ));

            if (bitmap == null)
                return null;

            using var orientedBitmap = origin != SKEncodedOrigin.TopLeft 
                ? ApplyOrientationOptimized(bitmap, origin)
                : bitmap;

            using var resizedBitmap = orientedBitmap.Resize(
                new SKImageInfo(targetWidth, targetHeight), 
                SKFilterQuality.Low
            );
            
            if (resizedBitmap == null)
                return null;

            using var image = SKImage.FromBitmap(resizedBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, ThumbnailQuality);

            await using var outputStream = File.OpenWrite(thumbnailPath);
            data.SaveTo(outputStream);

            if (origin == SKEncodedOrigin.TopLeft)
                bitmap.Dispose();

            return thumbnailPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur création miniature: {ex.Message}");
            return null;
        }
    }

    private int CalculateSampleSize(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        int sampleSize = 1;
        
        while (sourceWidth / (sampleSize * 2) >= targetWidth && 
               sourceHeight / (sampleSize * 2) >= targetHeight)
        {
            sampleSize *= 2;
        }
        
        return sampleSize;
    }

    private SKBitmap ApplyOrientationOptimized(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return bitmap;

        var surface = SKSurface.Create(new SKImageInfo(
            origin.IsRotate90or270() ? bitmap.Height : bitmap.Width,
            origin.IsRotate90or270() ? bitmap.Width : bitmap.Height
        ));

        using (var canvas = surface.Canvas)
        {
            canvas.Clear(SKColors.Transparent);

            switch (origin)
            {
                case SKEncodedOrigin.TopRight:
                    canvas.Scale(-1, 1, bitmap.Width / 2f, 0);
                    break;
                case SKEncodedOrigin.BottomRight:
                    canvas.RotateDegrees(180, bitmap.Width / 2f, bitmap.Height / 2f);
                    break;
                case SKEncodedOrigin.BottomLeft:
                    canvas.Scale(1, -1, 0, bitmap.Height / 2f);
                    break;
                case SKEncodedOrigin.RightTop:
                    canvas.Translate(bitmap.Height, 0);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.LeftBottom:
                    canvas.Translate(0, bitmap.Width);
                    canvas.RotateDegrees(-90);
                    break;
                case SKEncodedOrigin.LeftTop:
                case SKEncodedOrigin.RightBottom:
                    canvas.Translate(0, bitmap.Width);
                    canvas.RotateDegrees(-90);
                    break;
            }

            canvas.DrawBitmap(bitmap, 0, 0);
        }

        return SKBitmap.FromImage(surface.Snapshot());
    }

    private (int width, int height) CalculateThumbnailSize(int originalWidth, int originalHeight)
    {
        var ratio = (double)originalWidth / originalHeight;
        var width = (int)(ThumbnailHeight * ratio);
        return (width, ThumbnailHeight);
    }
}

internal static class SKEncodedOriginExtensions
{
    public static bool IsRotate90or270(this SKEncodedOrigin origin)
    {
        return origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;
    }
}
