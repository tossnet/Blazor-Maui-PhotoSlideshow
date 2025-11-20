using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Blazor.Maui.PhotoSlideshow.Services;

public class ImageCacheService
{
    private const string NetworkFolderKey = "NetworkFolderPath";
    private const string DefaultNetworkFolder = "";
    private const string ImageListCacheFile = "image_list_cache.json";

    private readonly string _thumbnailsFolder;
    private readonly string _imageListCachePath;
    private readonly SemaphoreSlim _cacheLock = new(10, 10);
    private readonly ThumbnailService _thumbnailService;
    private Task<List<string>>? _imageLoadingTask;
    private readonly ConcurrentBag<string> _discoveredImages = new();
    private FileSystemWatcher? _folderWatcher;

    public event Action<int>? OnImagesDiscovered;
    public event Action<string>? OnNewImageDetected;

    public string NetworkFolder
    {
        get => Preferences.Default.Get(NetworkFolderKey, DefaultNetworkFolder);
        set
        {
            Preferences.Default.Set(NetworkFolderKey, value);
            _imageLoadingTask = null;
            _discoveredImages.Clear();
            DeleteImageListCache();
            StopFolderWatcher();
        }
    }

    public bool IsNetworkFolderConfigured => !string.IsNullOrWhiteSpace(NetworkFolder);

    public ImageCacheService(ThumbnailService thumbnailService)
    {
        _thumbnailService = thumbnailService;
        _thumbnailsFolder = Path.Combine(FileSystem.CacheDirectory, "thumbnails");
        _imageListCachePath = Path.Combine(FileSystem.CacheDirectory, ImageListCacheFile);

        Directory.CreateDirectory(_thumbnailsFolder);
    }

    /// <summary>
    /// Récupère le chemin de la miniature (pour mosaïque)
    /// </summary>
    public async Task<string?> GetThumbnailPathAsync(string networkPath)
    {
        var cacheFileName = GetCacheFileName(networkPath, "_thumb");
        var thumbnailPath = Path.Combine(_thumbnailsFolder, cacheFileName);

        if (File.Exists(thumbnailPath))
            return thumbnailPath;

        return await CreateThumbnailFromNetworkAsync(networkPath, thumbnailPath);
    }

    /// <summary>
    /// Retourne directement le chemin réseau pour l'affichage plein écran
    /// Pas de cache - lecture directe depuis le réseau
    /// </summary>
    public Task<string?> GetFullSizeImagePathAsync(string networkPath)
    {
        // Vérifier que le fichier existe
        if (!File.Exists(networkPath))
        {
            Console.WriteLine($"⚠️ Fichier introuvable: {networkPath}");
            return Task.FromResult<string?>(null);
        }

        Console.WriteLine($"📥 Lecture directe depuis réseau: {Path.GetFileName(networkPath)}");

        // Retourner directement le chemin réseau
        return Task.FromResult<string?>(networkPath);
    }

    private async Task<string?> CreateThumbnailFromNetworkAsync(string networkPath, string thumbnailPath)
    {
        await _cacheLock.WaitAsync();
        try
        {
            if (File.Exists(thumbnailPath))
                return thumbnailPath;

            if (!File.Exists(networkPath))
                return null;

            return await _thumbnailService.CreateThumbnailAsync(networkPath, thumbnailPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur création miniature: {ex.Message}");
            return null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public void StartLoadingImagesInBackground()
    {
        if (_imageLoadingTask != null)
            return;

        _imageLoadingTask = Task.Run(async () =>
        {
            var networkFolder = NetworkFolder;

            if (string.IsNullOrWhiteSpace(networkFolder) || !Directory.Exists(networkFolder))
                return new List<string>();

            var cachedData = await LoadImageListFromCacheAsync(networkFolder);
            if (cachedData != null)
            {
                Console.WriteLine($"Cache chargé: {cachedData.Images.Count} images");

                foreach (var image in cachedData.Images)
                {
                    _discoveredImages.Add(image);
                }
                OnImagesDiscovered?.Invoke(_discoveredImages.Count);

                _ = Task.Run(() => IncrementalScanAsync(networkFolder, cachedData));
                StartFolderWatcher(networkFolder);

                return cachedData.Images;
            }

            var images = await ScanNetworkFolderAsync(networkFolder);
            await SaveImageListToCacheAsync(networkFolder, images);
            StartFolderWatcher(networkFolder);

            return images;
        });
    }

    private async Task IncrementalScanAsync(string networkFolder, ImageListCache cachedData)
    {
        try
        {
            var lastScanDate = cachedData.LastUpdate;
            var newImages = new List<string>();

            Console.WriteLine($"Scan incrémental depuis {lastScanDate}...");

            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                BufferSize = 16384
            };

            foreach (var file in Directory.EnumerateFiles(networkFolder, "*.*", enumerationOptions))
            {
                if (!IsImageFile(file))
                    continue;

                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime > lastScanDate || !cachedData.Images.Contains(file))
                {
                    if (!_discoveredImages.Contains(file))
                    {
                        newImages.Add(file);
                        _discoveredImages.Add(file);
                        OnNewImageDetected?.Invoke(file);
                        await Task.Delay(1);
                    }
                }
            }

            if (newImages.Count > 0)
            {
                Console.WriteLine($"🆕 {newImages.Count} nouvelles images détectées");
                var allImages = cachedData.Images.Concat(newImages).Distinct().ToList();
                await SaveImageListToCacheAsync(networkFolder, allImages);
                OnImagesDiscovered?.Invoke(_discoveredImages.Count);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur scan incrémental: {ex.Message}");
        }
    }

    private void StartFolderWatcher(string networkFolder)
    {
        try
        {
            StopFolderWatcher();

            _folderWatcher = new FileSystemWatcher(networkFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _folderWatcher.Created += async (s, e) =>
            {
                if (IsImageFile(e.FullPath))
                {
                    await Task.Delay(500);
                    if (!_discoveredImages.Contains(e.FullPath))
                    {
                        _discoveredImages.Add(e.FullPath);
                        OnNewImageDetected?.Invoke(e.FullPath);
                        OnImagesDiscovered?.Invoke(_discoveredImages.Count);

                        var allImages = _discoveredImages.ToList();
                        await SaveImageListToCacheAsync(networkFolder, allImages);
                    }
                }
            };

            Console.WriteLine("👁️ Surveillance activée");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Impossible de surveiller: {ex.Message}");
        }
    }

    private void StopFolderWatcher()
    {
        if (_folderWatcher != null)
        {
            _folderWatcher.EnableRaisingEvents = false;
            _folderWatcher.Dispose();
            _folderWatcher = null;
        }
    }

    private async Task<List<string>> ScanNetworkFolderAsync(string networkFolder)
    {
        var images = new List<string>();

        try
        {
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                BufferSize = 16384
            };

            int batchCount = 0;
            foreach (var file in Directory.EnumerateFiles(networkFolder, "*.*", enumerationOptions))
            {
                if (IsImageFile(file))
                {
                    images.Add(file);
                    _discoveredImages.Add(file);
                    batchCount++;

                    if (batchCount % 50 == 0)
                    {
                        OnImagesDiscovered?.Invoke(_discoveredImages.Count);
                        await Task.Delay(1);
                    }
                }
            }

            OnImagesDiscovered?.Invoke(_discoveredImages.Count);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur chargement: {ex.Message}");
        }

        return images;
    }

    private async Task<ImageListCache?> LoadImageListFromCacheAsync(string networkFolder)
    {
        try
        {
            if (!File.Exists(_imageListCachePath))
                return null;

            var json = await File.ReadAllTextAsync(_imageListCachePath);
            var cache = JsonSerializer.Deserialize<ImageListCache>(json);

            if (cache?.NetworkFolder != networkFolder)
                return null;

            var absoluteImages = cache.Images
                .Select(relativePath => Path.Combine(networkFolder, relativePath))
                .ToList();

            cache.Images = absoluteImages;

            if (cache.Images.Count > 0)
            {
                var sampleSize = Math.Min(10, cache.Images.Count);
                var sample = cache.Images.Take(sampleSize);
                if (sample.All(img => !File.Exists(img)))
                {
                    Console.WriteLine("⚠️ Cache invalide");
                    return null;
                }
            }

            return cache;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur lecture cache: {ex.Message}");
            return null;
        }
    }

    private async Task SaveImageListToCacheAsync(string networkFolder, List<string> images)
    {
        try
        {
            var relativeImages = images
                .Select(path => Path.GetRelativePath(networkFolder, path))
                .ToList();

            var cache = new ImageListCache
            {
                NetworkFolder = networkFolder,
                LastUpdate = DateTime.Now,
                Images = relativeImages
            };

            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = false });

            var jsonSizeMb = json.Length / (1024.0 * 1024.0);
            Console.WriteLine($"💾 Cache: {images.Count} images, {jsonSizeMb:F2} Mo");

            await File.WriteAllTextAsync(_imageListCachePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur sauvegarde: {ex.Message}");
        }
    }

    private void DeleteImageListCache()
    {
        try
        {
            if (File.Exists(_imageListCachePath))
                File.Delete(_imageListCachePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur suppression: {ex.Message}");
        }
    }

    public List<string> GetDiscoveredImages() => _discoveredImages.ToList();

    public async Task<List<string>> GetAllNetworkImagesAsync()
    {
        if (_imageLoadingTask == null)
            StartLoadingImagesInBackground();

        return await _imageLoadingTask!;
    }

    public bool IsLoadingComplete => _imageLoadingTask?.IsCompleted ?? false;

    public async Task RefreshImageListAsync()
    {
        StopFolderWatcher();
        _imageLoadingTask = null;
        _discoveredImages.Clear();
        DeleteImageListCache();
        StartLoadingImagesInBackground();
        await GetAllNetworkImagesAsync();
    }

    private bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLower();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif";
    }

    private string GetCacheFileName(string networkPath, string suffix = "")
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(networkPath));
        var hashString = BitConverter.ToString(hash).Replace("-", "").ToLower();
        return $"{hashString}{suffix}{Path.GetExtension(networkPath)}";
    }

    public void ClearCache()
    {
        try
        {
            StopFolderWatcher();
            _discoveredImages.Clear();
            _imageLoadingTask = null;
            DeleteImageListCache();

            if (Directory.Exists(_thumbnailsFolder))
            {
                Directory.Delete(_thumbnailsFolder, true);
                Directory.CreateDirectory(_thumbnailsFolder);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur nettoyage: {ex.Message}");
        }
    }

    /// <summary>
    /// Méthode de compatibilité - retourne les thumbnails
    /// </summary>
    public async Task<string?> GetCachedImagePathAsync(string networkPath)
    {
        return await GetThumbnailPathAsync(networkPath);
    }

    private class ImageListCache
    {
        public string NetworkFolder { get; set; } = string.Empty;
        public DateTime LastUpdate { get; set; }
        public List<string> Images { get; set; } = new();
    }
}