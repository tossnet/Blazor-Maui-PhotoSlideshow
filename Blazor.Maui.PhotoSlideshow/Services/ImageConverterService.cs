using System.Collections.Concurrent;

namespace Blazor.Maui.PhotoSlideshow.Services;

public class ImageConverterService
{
    private readonly ConcurrentDictionary<string, string> _base64Cache = new();

    public string ConvertToBase64(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
            return string.Empty;

        // Retourner du cache si disponible
        if (_base64Cache.TryGetValue(imagePath, out var cached))
            return cached;

        try
        {
            if (!File.Exists(imagePath))
                return string.Empty;

            var bytes = File.ReadAllBytes(imagePath);
            var base64 = Convert.ToBase64String(bytes);
            var ext = Path.GetExtension(imagePath).ToLower();

            var mimeType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "image/jpeg"
            };

            var result = $"data:{mimeType};base64,{base64}";

            // Mettre en cache
            _base64Cache.TryAdd(imagePath, result);

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur conversion image: {ex.Message}");
            return string.Empty;
        }
    }

    public void ClearCache()
    {
        _base64Cache.Clear();
    }
}