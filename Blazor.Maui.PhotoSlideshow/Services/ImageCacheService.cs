using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;

namespace Blazor.Maui.PhotoSlideshow.Services;

public class ImageCacheService
{
    private const string NetworkFolderKey = "NetworkFolderPath";
    private const string DefaultNetworkFolder = "";

    private readonly string _cacheFolder;
    private readonly SemaphoreSlim _cacheLock = new(10, 10);
    private Task<List<string>>? _imageLoadingTask;
    private readonly ConcurrentBag<string> _discoveredImages = new();

    public event Action<int>? OnImagesDiscovered;

    public string NetworkFolder
    {
        get => Preferences.Default.Get(NetworkFolderKey, DefaultNetworkFolder);
        set
        {
            Preferences.Default.Set(NetworkFolderKey, value);
            // Réinitialiser le chargement si le chemin change
            _imageLoadingTask = null;
            _discoveredImages.Clear();
        }
    }

    public bool IsNetworkFolderConfigured => !string.IsNullOrWhiteSpace(NetworkFolder);

    public ImageCacheService()
    {
        _cacheFolder = Path.Combine(FileSystem.CacheDirectory, "images");
        Directory.CreateDirectory(_cacheFolder);
    }

    public async Task<string?> GetCachedImagePathAsync(string networkPath)
    {
        var cacheFileName = GetCacheFileName(networkPath);
        var cachePath = Path.Combine(_cacheFolder, cacheFileName);

        if (File.Exists(cachePath))
        {
            return cachePath;
        }

        return await CacheImageAsync(networkPath, cachePath);
    }

    private async Task<string?> CacheImageAsync(string networkPath, string cachePath)
    {
        await _cacheLock.WaitAsync();
        try
        {
            if (File.Exists(cachePath))
                return cachePath;

            if (!File.Exists(networkPath))
                return null;

            using var sourceStream = new FileStream(networkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var destStream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await sourceStream.CopyToAsync(destStream);

            return cachePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur cache image: {ex.Message}");
            return null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Démarre le chargement des images en arrière-plan sans bloquer
    /// </summary>
    public void StartLoadingImagesInBackground()
    {
        if (_imageLoadingTask != null)
            return;

        _imageLoadingTask = Task.Run(async () =>
        {
            var images = new List<string>();
            var networkFolder = NetworkFolder;

            if (string.IsNullOrWhiteSpace(networkFolder) || !Directory.Exists(networkFolder))
                return images;

            try
            {
                // Énumération progressive avec notifications
                var enumerationOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    BufferSize = 16384 // Buffer optimisé pour réseau
                };

                int batchCount = 0;
                foreach (var file in Directory.EnumerateFiles(networkFolder, "*.*", enumerationOptions))
                {
                    if (IsImageFile(file))
                    {
                        images.Add(file);
                        _discoveredImages.Add(file);
                        batchCount++;

                        // Notifier tous les 50 fichiers pour permettre l'affichage progressif
                        if (batchCount % 50 == 0)
                        {
                            OnImagesDiscovered?.Invoke(_discoveredImages.Count);
                            await Task.Delay(1); // Permet aux autres tâches de s'exécuter
                        }
                    }
                }

                // Notification finale
                OnImagesDiscovered?.Invoke(_discoveredImages.Count);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur chargement images: {ex.Message}");
            }

            return images;
        });
    }

    /// <summary>
    /// Retourne les images découvertes jusqu'à présent (non bloquant)
    /// </summary>
    public List<string> GetDiscoveredImages()
    {
        return _discoveredImages.ToList();
    }

    /// <summary>
    /// Attend que toutes les images soient chargées (si nécessaire)
    /// </summary>
    public async Task<List<string>> GetAllNetworkImagesAsync()
    {
        if (_imageLoadingTask == null)
        {
            StartLoadingImagesInBackground();
        }

        return await _imageLoadingTask!;
    }

    /// <summary>
    /// Vérifie si le chargement est terminé
    /// </summary>
    public bool IsLoadingComplete => _imageLoadingTask?.IsCompleted ?? false;

    private bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLower();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif";
    }

    private string GetCacheFileName(string networkPath)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(networkPath));
        var hashString = BitConverter.ToString(hash).Replace("-", "").ToLower();
        var extension = Path.GetExtension(networkPath);
        return $"{hashString}{extension}";
    }

    public async Task PreloadImagesAsync(List<string> imagePaths, int maxConcurrent = 5)
    {
        var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        var tasks = imagePaths.Select(async path =>
        {
            await semaphore.WaitAsync();
            try
            {
                await GetCachedImagePathAsync(path);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    public void ClearCache()
    {
        try
        {
            _discoveredImages.Clear();
            _imageLoadingTask = null;
            if (Directory.Exists(_cacheFolder))
            {
                Directory.Delete(_cacheFolder, true);
                Directory.CreateDirectory(_cacheFolder);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur nettoyage cache: {ex.Message}");
        }
    }
}