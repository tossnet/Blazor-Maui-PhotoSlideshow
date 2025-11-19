using System.Collections.Concurrent;

namespace Blazor.Maui.PhotoSlideshow.Services;

public class ImageConverterService
{
    private readonly ConcurrentDictionary<string, string> _base64Cache = new();
    private readonly ConcurrentDictionary<string, Task<string>> _pendingConversions = new();
    
    // OPTIMISÉ: Limite de cache pour éviter l'explosion mémoire
    private const int MAX_CACHE_SIZE = 200; // Limite à 200 images en cache
    private readonly Queue<string> _cacheKeys = new();
    private readonly object _cacheLock = new();

    /// <summary>
    /// Convertit de manière ASYNCHRONE pour éviter de bloquer le thread UI
    /// OPTIMISÉ: Avec limite de cache pour éviter OOM
    /// </summary>
    public async Task<string> ConvertToBase64Async(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
            return string.Empty;

        // Retourner du cache si disponible
        if (_base64Cache.TryGetValue(imagePath, out var cached))
            return cached;

        // Si une conversion est déjà en cours, attendre le résultat
        if (_pendingConversions.TryGetValue(imagePath, out var pendingTask))
            return await pendingTask;

        // Créer une nouvelle tâche de conversion
        var conversionTask = Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(imagePath))
                    return string.Empty;

                // Lecture asynchrone pour ne pas bloquer
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

                // OPTIMISÉ: Mettre en cache avec limite LRU (Least Recently Used)
                AddToCache(imagePath, result);

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur conversion image {Path.GetFileName(imagePath)}: {ex.Message}");
                return string.Empty;
            }
            finally
            {
                // Nettoyer la tâche en attente
                _pendingConversions.TryRemove(imagePath, out _);
            }
        });

        // Enregistrer la tâche en cours
        _pendingConversions.TryAdd(imagePath, conversionTask);

        return await conversionTask;
    }

    /// <summary>
    /// OPTIMISÉ: Ajoute au cache avec politique LRU pour éviter l'explosion mémoire
    /// </summary>
    private void AddToCache(string key, string value)
    {
        lock (_cacheLock)
        {
            // Si le cache est plein, supprimer le plus ancien
            if (_base64Cache.Count >= MAX_CACHE_SIZE && !_base64Cache.ContainsKey(key))
            {
                if (_cacheKeys.TryDequeue(out var oldestKey))
                {
                    _base64Cache.TryRemove(oldestKey, out _);
                    Console.WriteLine($"Cache LRU: Suppression de {Path.GetFileName(oldestKey)}");
                }
            }

            // Ajouter la nouvelle entrée
            if (_base64Cache.TryAdd(key, value))
            {
                _cacheKeys.Enqueue(key);
            }
        }
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _base64Cache.Clear();
            _pendingConversions.Clear();
            _cacheKeys.Clear();
            
            // Forcer la collecte garbage pour libérer la mémoire
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    /// <summary>
    /// Précharge une image dans le cache de manière asynchrone
    /// </summary>
    public Task PreloadImageAsync(string imagePath)
    {
        return ConvertToBase64Async(imagePath);
    }

    /// <summary>
    /// NOUVEAU: Obtenir les statistiques du cache
    /// </summary>
    public (int count, int pending) GetCacheStats()
    {
        return (_base64Cache.Count, _pendingConversions.Count);
    }
}