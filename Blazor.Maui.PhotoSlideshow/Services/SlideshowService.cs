using Blazor.Maui.PhotoSlideshow.Models;

namespace Blazor.Maui.PhotoSlideshow.Services;

public class SlideshowService
{
    private readonly ImageCacheService _cacheService;
    private List<ImageItem> _images = new();
    private List<string> _allNetworkImages = new();
    private Random _random = new();
    private System.Timers.Timer? _fullscreenTimer;
    private System.Timers.Timer? _loadingTimer;
    private int _currentFullscreenIndex = -1;
    private int _lastProcessedDiscoveredCount = 0;
    private bool _isLoadingComplete = false;
    private readonly SemaphoreSlim _loadingSemaphore = new(3, 3);
    private string? _nextFullscreenImagePath; // Préchargement
    private bool _isAnimationRunning = false;

    // AUGMENTÉ : Afficher beaucoup de miniatures pour créer un grand mur
    private const int MAX_VISIBLE_IMAGES = 150; // Grand mur de miniatures

    public event Action? OnImagesChanged;
    public event Action<int>? OnFullScreenChanged;

    public List<ImageItem> Images => _images;
    public int FullScreenInterval { get; set; } = 5000;
    public int TotalImages => _allNetworkImages.Count;
    public int LoadedImages => _lastProcessedDiscoveredCount;
    public bool IsLoadingComplete => _isLoadingComplete;
    public bool IsAnimationRunning => _isAnimationRunning;

    public SlideshowService(ImageCacheService cacheService)
    {
        _cacheService = cacheService;
        _cacheService.OnImagesDiscovered += OnNetworkImagesDiscovered;
    }

    public Task InitializeAsync()
    {
        _cacheService.StartLoadingImagesInBackground();
        StartProgressiveLoading();
        return Task.CompletedTask;
    }

    private void OnNetworkImagesDiscovered(int count)
    {
        _allNetworkImages = _cacheService.GetDiscoveredImages();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnImagesChanged?.Invoke();
        });
    }

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index >= _allNetworkImages.Count)
            return;

        if (_images.Count >= MAX_VISIBLE_IMAGES)
            return;

        await _loadingSemaphore.WaitAsync();
        try
        {
            var networkPath = _allNetworkImages[index];

            if (_images.Any(i => i.NetworkPath == networkPath))
                return;

            var item = new ImageItem
            {
                NetworkPath = networkPath,
                Opacity = 1.0
            };

            // Charger la MINIATURE pour la mosaïque (rapide et léger)
            item.CachedPath = await _cacheService.GetThumbnailPathAsync(item.NetworkPath);

            if (!string.IsNullOrEmpty(item.CachedPath))
            {
                _images.Add(item);
            }
        }
        finally
        {
            _loadingSemaphore.Release();
        }
    }

    private void StartProgressiveLoading()
    {
        _loadingTimer = new System.Timers.Timer(300);
        _loadingTimer.Elapsed += async (s, e) =>
        {
            try
            {
                var discoveredImages = _cacheService.GetDiscoveredImages();

                if (discoveredImages.Count > 0)
                {
                    _allNetworkImages = discoveredImages;
                }

                if (_images.Count >= MAX_VISIBLE_IMAGES)
                {
                    _isLoadingComplete = true;
                    _loadingTimer?.Stop();

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        OnImagesChanged?.Invoke();
                    });
                    return;
                }

                if (_lastProcessedDiscoveredCount < _allNetworkImages.Count)
                {
                    var remainingSlots = MAX_VISIBLE_IMAGES - _images.Count;
                    var batchSize = Math.Min(20, remainingSlots);
                    var imagesToLoadCount = Math.Min(batchSize, _allNetworkImages.Count - _lastProcessedDiscoveredCount);

                    var tasks = Enumerable.Range(_lastProcessedDiscoveredCount, imagesToLoadCount)
                        .Select(i => LoadImageAtIndexAsync(i))
                        .ToList();

                    await Task.WhenAll(tasks);

                    _lastProcessedDiscoveredCount += imagesToLoadCount;

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        OnImagesChanged?.Invoke();
                    });
                }

                if (_cacheService.IsLoadingComplete && _lastProcessedDiscoveredCount >= _allNetworkImages.Count)
                {
                    _loadingTimer?.Stop();
                    _isLoadingComplete = true;

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        OnImagesChanged?.Invoke();
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur chargement progressif: {ex.Message}");
            }
        };
        _loadingTimer.Start();
    }

    public void StartAnimation()
    {
        _isAnimationRunning = true;

        _fullscreenTimer = new System.Timers.Timer(FullScreenInterval);
        _fullscreenTimer.Elapsed += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() => ShowNextFullscreen());
        };
        _fullscreenTimer.Start();

        // Précharger la première image plein écran
        _ = PreloadNextFullscreenImageAsync();

        Console.WriteLine("🚀 Animation démarrée !");
        OnImagesChanged?.Invoke();
    }

    public void StopAnimation()
    {
        _isAnimationRunning = false;
        _fullscreenTimer?.Stop();
        Console.WriteLine("⏸️ Animation arrêtée");
        OnImagesChanged?.Invoke();
    }

    private async Task PreloadNextFullscreenImageAsync()
    {
        try
        {
            if (!_images.Any())
                return;

            var nextIndex = _random.Next(_images.Count);
            var nextImage = _images[nextIndex];

            Console.WriteLine($"🔄 Préchargement plein écran: {Path.GetFileName(nextImage.NetworkPath)}");

            _nextFullscreenImagePath = await _cacheService.GetFullSizeImagePathAsync(nextImage.NetworkPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur préchargement: {ex.Message}");
        }
    }

    private void ShowNextFullscreen()
    {
        if (!_images.Any())
            return;

        // Retirer l'ancienne image fullscreen et la remplacer
        if (_currentFullscreenIndex >= 0 && _currentFullscreenIndex < _images.Count)
        {
            var oldImage = _images[_currentFullscreenIndex];
            oldImage.IsFullScreen = false;
            oldImage.FullscreenPath = null;

            // Remplacer par une nouvelle thumbnail
            ReplaceImageWithNewOne(_currentFullscreenIndex);
        }

        // Afficher la nouvelle image plein écran
        _currentFullscreenIndex = _random.Next(_images.Count);
        var currentImage = _images[_currentFullscreenIndex];
        currentImage.IsFullScreen = true;

        if (!string.IsNullOrEmpty(_nextFullscreenImagePath))
        {
            currentImage.FullscreenPath = _nextFullscreenImagePath;
            Console.WriteLine($"✅ Affichage plein écran: {Path.GetFileName(currentImage.NetworkPath)}");
        }

        OnFullScreenChanged?.Invoke(_currentFullscreenIndex);

        _ = PreloadNextFullscreenImageAsync();
    }

    private async void ReplaceImageWithNewOne(int indexToReplace)
    {
        try
        {
            var availableImages = _allNetworkImages
                .Where(path => !_images.Any(i => i.NetworkPath == path))
                .ToList();

            if (!availableImages.Any())
            {
                availableImages = _allNetworkImages;
            }

            var newPath = availableImages[_random.Next(availableImages.Count)];

            var item = new ImageItem
            {
                NetworkPath = newPath,
                Opacity = 1.0
            };

            item.CachedPath = await _cacheService.GetThumbnailPathAsync(item.NetworkPath);

            if (!string.IsNullOrEmpty(item.CachedPath))
            {
                _images[indexToReplace] = item;
                Console.WriteLine($"🔄 Miniature remplacée: {Path.GetFileName(newPath)}");
                OnImagesChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur remplacement image: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cacheService.OnImagesDiscovered -= OnNetworkImagesDiscovered;
        _fullscreenTimer?.Dispose();
        _loadingTimer?.Dispose();
        _loadingSemaphore?.Dispose();
    }
}