//using Android.Graphics;
//using static Android.Graphics.ImageDecoder;
using SkiaSharp;

namespace Blazor.Maui.PhotoSlideshow.Services;

public class ThumbnailService
{
    // OPTIMISÉ: Réduit de 300px → 200px pour moins de CPU/mémoire
    private const int ThumbnailHeight = 200;
    // OPTIMISÉ: Réduit de 75 → 60 pour fichiers plus petits
    private const int ThumbnailQuality = 60;

    /// <summary>
    /// Crée une miniature optimisée pour l'affichage en mosaïque
    /// OPTIMISÉ: Décodage direct à la taille cible + orientation simplifiée
    /// </summary>
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

            // OPTIMISÉ: Calculer la taille cible AVANT le décodage
            var originalWidth = codec.Info.Width;
            var originalHeight = codec.Info.Height;
            
            // Ajuster pour l'orientation
            if (origin.IsRotate90or270())
            {
                (originalWidth, originalHeight) = (originalHeight, originalWidth);
            }

            var (targetWidth, targetHeight) = CalculateThumbnailSize(originalWidth, originalHeight);

            // OPTIMISÉ: Décoder directement à une taille réduite (plus rapide que decode puis resize)
            // Utiliser le sous-échantillonnage pour décoder moins de pixels
            var sampleSize = CalculateSampleSize(codec.Info.Width, codec.Info.Height, targetWidth, targetHeight);
            
            var bitmap = SKBitmap.Decode(codec, new SKImageInfo(
                codec.Info.Width / sampleSize,
                codec.Info.Height / sampleSize,
                SKColorType.Rgba8888
            ));

            if (bitmap == null)
                return null;

            // Appliquer l'orientation si nécessaire
            using var orientedBitmap = origin != SKEncodedOrigin.TopLeft 
                ? ApplyOrientationOptimized(bitmap, origin)
                : bitmap;

            // Resize final à la taille exacte souhaitée (depuis une taille déjà réduite)
            using var resizedBitmap = orientedBitmap.Resize(
                new SKImageInfo(targetWidth, targetHeight), 
                SKFilterQuality.Low  // OPTIMISÉ: Low au lieu de Medium (plus rapide)
            );
            
            if (resizedBitmap == null)
                return null;

            using var image = SKImage.FromBitmap(resizedBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, ThumbnailQuality);

            await using var outputStream = File.OpenWrite(thumbnailPath);
            data.SaveTo(outputStream);

            // Cleanup du bitmap original si différent de orientedBitmap
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

    /// <summary>
    /// OPTIMISÉ: Calcule le facteur de sous-échantillonnage pour décoder moins de pixels
    /// </summary>
    private int CalculateSampleSize(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        int sampleSize = 1;
        
        // Trouver la plus grande puissance de 2 qui garde l'image >= taille cible
        while (sourceWidth / (sampleSize * 2) >= targetWidth && 
               sourceHeight / (sampleSize * 2) >= targetHeight)
        {
            sampleSize *= 2;
        }
        
        return sampleSize;
    }

    /// <summary>
    /// OPTIMISÉ: Version simplifiée de l'orientation (moins de transformations)
    /// </summary>
    private SKBitmap ApplyOrientationOptimized(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        // Pour les orientations simples, on peut éviter certaines transformations
        if (origin == SKEncodedOrigin.TopLeft)
            return bitmap;

        var surface = SKSurface.Create(new SKImageInfo(
            origin.IsRotate90or270() ? bitmap.Height : bitmap.Width,
            origin.IsRotate90or270() ? bitmap.Width : bitmap.Height
        ));

        using (var canvas = surface.Canvas)
        {
            canvas.Clear(SKColors.Transparent);

            // Appliquer uniquement les transformations essentielles
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
                // OPTIMISÉ: Orientations moins courantes simplifiées
                case SKEncodedOrigin.LeftTop:
                case SKEncodedOrigin.RightBottom:
                    // Fallback: rotation simple
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