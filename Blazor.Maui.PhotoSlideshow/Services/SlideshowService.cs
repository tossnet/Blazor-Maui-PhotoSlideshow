using Blazor.Maui.PhotoSlideshow.Models;

namespace Blazor.Maui.PhotoSlideshow.Services;

public class SlideshowService
{
    private readonly ImageCacheService _cacheService;
    private List<ImageItem> _images = new();
    private List<string> _allNetworkImages = new();
    private Random _random = new();
    private System.Timers.Timer? _animationTimer;
    private System.Timers.Timer? _fullscreenTimer;
    private System.Timers.Timer? _loadingTimer;
    private int _currentFullscreenIndex = -1;
    private int _lastProcessedDiscoveredCount = 0;
    private bool _isLoadingComplete = false;
    private readonly SemaphoreSlim _loadingSemaphore = new(3, 3);

    // LIMITE CRITIQUE : Nombre maximum d'images en mémoire
    private const int MAX_VISIBLE_IMAGES = 50; // Réduit drastiquement pour éviter l'overflow JSON

    public event Action? OnImagesChanged;
    public event Action<int>? OnFullScreenChanged;

    public List<ImageItem> Images => _images;
    public int FullScreenInterval { get; set; } = 5000;
    public int TotalImages => _allNetworkImages.Count;
    public int LoadedImages => _lastProcessedDiscoveredCount;
    public bool IsLoadingComplete => _isLoadingComplete;

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

        // NE PAS CHARGER si on a déjà trop d'images
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
                X = _random.NextDouble() * 80 + 10,
                Y = _random.NextDouble() * 80 + 10,
                Scale = 0.3 + _random.NextDouble() * 0.3,
                Rotation = _random.NextDouble() * 360
            };

            item.CachedPath = await _cacheService.GetCachedImagePathAsync(item.NetworkPath);

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
        _loadingTimer = new System.Timers.Timer(1000); // Ralenti à 1 seconde
        _loadingTimer.Elapsed += async (s, e) =>
        {
            try
            {
                // Arrêter si on a atteint la limite
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

                var discoveredImages = _cacheService.GetDiscoveredImages();

                if (discoveredImages.Count > _lastProcessedDiscoveredCount)
                {
                    _allNetworkImages = discoveredImages;

                    // Charger seulement 5 images à la fois (réduit de 10)
                    var remainingSlots = MAX_VISIBLE_IMAGES - _images.Count;
                    var batchSize = Math.Min(5, remainingSlots);

                    var imagesToLoad = _allNetworkImages
                        .Skip(_lastProcessedDiscoveredCount)
                        .Take(batchSize)
                        .ToList();

                    if (imagesToLoad.Any())
                    {
                        var tasks = imagesToLoad
                            .Select(path => LoadImageAtIndexAsync(_allNetworkImages.IndexOf(path)))
                            .ToList();

                        await Task.WhenAll(tasks);

                        _lastProcessedDiscoveredCount = Math.Min(
                            _lastProcessedDiscoveredCount + batchSize,
                            _allNetworkImages.Count
                        );

                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            OnImagesChanged?.Invoke();
                        });
                    }
                }

                // Le scan continue en arrière-plan mais on arrête de charger des images
                if (_cacheService.IsLoadingComplete)
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
        _animationTimer = new System.Timers.Timer(50);
        _animationTimer.Elapsed += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() => AnimateImages());
        };
        _animationTimer.Start();

        _fullscreenTimer = new System.Timers.Timer(FullScreenInterval);
        _fullscreenTimer.Elapsed += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() => ShowNextFullscreen());
        };
        _fullscreenTimer.Start();
    }

    public void StopAnimation()
    {
        _animationTimer?.Stop();
        _fullscreenTimer?.Stop();
    }

    private void AnimateImages()
    {
        foreach (var img in _images.Where(i => !i.IsFullScreen))
        {
            var angle = (DateTime.Now.Ticks / 10000000.0) + img.Rotation;

            img.X += Math.Cos(angle) * 0.5;
            img.Y += Math.Sin(angle) * 0.5;

            if (img.X < -10) img.X = 110;
            if (img.X > 110) img.X = -10;
            if (img.Y < -10) img.Y = 110;
            if (img.Y > 110) img.Y = -10;

            img.Rotation += 0.2;
        }

        OnImagesChanged?.Invoke();
    }

    private void ShowNextFullscreen()
    {
        if (!_images.Any())
            return;

        // Retirer l'ancienne image fullscreen et charger une nouvelle aléatoire
        if (_currentFullscreenIndex >= 0 && _currentFullscreenIndex < _images.Count)
        {
            var oldImage = _images[_currentFullscreenIndex];
            oldImage.IsFullScreen = false;

            // Remplacer l'ancienne image par une nouvelle du pool disponible
            if (_allNetworkImages.Count > MAX_VISIBLE_IMAGES)
            {
                ReplaceImageWithNewOne(_currentFullscreenIndex);
            }
        }

        _currentFullscreenIndex = _random.Next(_images.Count);
        _images[_currentFullscreenIndex].IsFullScreen = true;

        OnFullScreenChanged?.Invoke(_currentFullscreenIndex);
    }

    private async void ReplaceImageWithNewOne(int indexToReplace)
    {
        try
        {
            // Choisir une image aléatoire non encore affichée
            var availableImages = _allNetworkImages
                .Where(path => !_images.Any(i => i.NetworkPath == path))
                .ToList();

            if (!availableImages.Any())
                return;

            var newPath = availableImages[_random.Next(availableImages.Count)];

            var item = new ImageItem
            {
                NetworkPath = newPath,
                X = _random.NextDouble() * 80 + 10,
                Y = _random.NextDouble() * 80 + 10,
                Scale = 0.3 + _random.NextDouble() * 0.3,
                Rotation = _random.NextDouble() * 360
            };

            item.CachedPath = await _cacheService.GetCachedImagePathAsync(item.NetworkPath);

            if (!string.IsNullOrEmpty(item.CachedPath))
            {
                _images[indexToReplace] = item;
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
        _animationTimer?.Dispose();
        _fullscreenTimer?.Dispose();
        _loadingTimer?.Dispose();
        _loadingSemaphore?.Dispose();
    }
}