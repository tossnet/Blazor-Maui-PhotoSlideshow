using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace BenchmarkSuite1.Services;

public class ImageConverterService
{
    private readonly ConcurrentDictionary<string, string> _base64Cache = new();
    private readonly ConcurrentDictionary<string, Task<string>> _pendingConversions = new();

    public async Task<string> ConvertToBase64Async(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
            return string.Empty;

        if (_base64Cache.TryGetValue(imagePath, out var cached))
            return cached;

        if (_pendingConversions.TryGetValue(imagePath, out var pendingTask))
            return await pendingTask;

        var conversionTask = Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(imagePath))
                    return string.Empty;

                var bytes = await File.ReadAllBytesAsync(imagePath);
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
                _base64Cache.TryAdd(imagePath, result);

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur conversion image {Path.GetFileName(imagePath)}: {ex.Message}");
                return string.Empty;
            }
            finally
            {
                _pendingConversions.TryRemove(imagePath, out _);
            }
        });

        _pendingConversions.TryAdd(imagePath, conversionTask);
        return await conversionTask;
    }

    public void ClearCache()
    {
        _base64Cache.Clear();
        _pendingConversions.Clear();
    }

    public Task PreloadImageAsync(string imagePath)
    {
        return ConvertToBase64Async(imagePath);
    }
}
