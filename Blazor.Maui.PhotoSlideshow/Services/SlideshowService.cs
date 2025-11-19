using Blazor.Maui.PhotoSlideshow.Models;

namespace Blazor.Maui.PhotoSlideshow.Services;

public class SlideshowService
{
    private readonly ImageCacheService _cacheService;
    private List<ImageItem> _images = new();
    private List<string> _allNetworkImages = new();
    private Random _random = new();
    private System.Timers.Timer? _cycleTimer;
    private System.Timers.Timer? _loadingTimer;
    private int _currentFullscreenIndex = -1;
    private int _lastProcessedDiscoveredCount = 0;
    private bool _isLoadingComplete = false;
    private readonly SemaphoreSlim _loadingSemaphore = new(3, 3);
    private string? _nextFullscreenImagePath; // Préchargement
    private bool _isAnimationRunning = false;
    private bool _isShowingFullscreen = false;
    private HashSet<string> _displayedImages = new(); // Track des images déjà affichées

    // AUGMENTÉ : Afficher beaucoup de miniatures pour créer un grand mur
    private const int MAX_VISIBLE_IMAGES = 150; // Grand mur de miniatures
    private const int INITIAL_RANDOM_COUNT = 30; // Nombre d'images aléatoires au démarrage

    public event Action? OnImagesChanged;
    public event Action<int>? OnFullScreenChanged;

    public List<ImageItem> Images => _images;
    public int MosaicDisplayDuration { get; set; } = 5000; // 5 secondes de mosaïque
    public int FullscreenDisplayDuration { get; set; } = 3000; // 3 secondes en plein écran
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

    /// <summary>
    /// Sélectionne des images aléatoires non encore affichées
    /// </summary>
    private List<string> GetRandomUnusedImages(int count)
    {
        var unusedImages = _allNetworkImages
            .Where(path => !_displayedImages.Contains(path))
            .ToList();

        // Si pas assez d'images non utilisées, réinitialiser
        if (unusedImages.Count < count)
        {
            _displayedImages.Clear();
            unusedImages = _allNetworkImages.ToList();
            Console.WriteLine("🔄 Réinitialisation du pool d'images");
        }

        // Mélanger et prendre les N premières
        var shuffled = unusedImages.OrderBy(_ => _random.Next()).Take(count).ToList();

        // Marquer comme affichées
        foreach (var img in shuffled)
        {
            _displayedImages.Add(img);
        }

        return shuffled;
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
                _displayedImages.Add(networkPath); // Marquer comme affichée
            }
        }
        finally
        {
            _loadingSemaphore.Release();
        }
    }

    /// <summary>
    /// Charge un lot d'images aléatoires
    /// </summary>
    private async Task LoadRandomImagesAsync(List<string> imagePaths)
    {
        var tasks = imagePaths.Select(async path =>
        {
            if (_images.Count >= MAX_VISIBLE_IMAGES)
                return;

            await _loadingSemaphore.WaitAsync();
            try
            {
                if (_images.Any(i => i.NetworkPath == path))
                    return;

                var item = new ImageItem
                {
                    NetworkPath = path,
                    Opacity = 1.0
                };

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
        });

        await Task.WhenAll(tasks);
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

                    // Au tout premier chargement, charger des images aléatoires
                    if (_images.Count == 0 && _allNetworkImages.Count >= INITIAL_RANDOM_COUNT)
                    {
                        Console.WriteLine($"🎲 Chargement initial de {INITIAL_RANDOM_COUNT} images aléatoires");
                        var randomImages = GetRandomUnusedImages(INITIAL_RANDOM_COUNT);
                        await LoadRandomImagesAsync(randomImages);
                        _lastProcessedDiscoveredCount = 0; // On continuera après avec les suivantes

                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            OnImagesChanged?.Invoke();
                        });
                        return;
                    }
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
        _isShowingFullscreen = false;

        // Précharger la première image plein écran
        _ = PreloadNextFullscreenImageAsync();

        // Démarrer le cycle : mosaïque → plein écran → mosaïque...
        StartCycleTimer();

        Console.WriteLine("🚀 Animation démarrée !");
        OnImagesChanged?.Invoke();
    }

    public void StopAnimation()
    {
        _isAnimationRunning = false;
        _cycleTimer?.Stop();

        // Fermer l'image plein écran si elle est affichée
        if (_isShowingFullscreen)
        {
            HideFullscreen();
        }

        Console.WriteLine("⏸️ Animation arrêtée");
        OnImagesChanged?.Invoke();
    }

    /// <summary>
    /// Démarre le cycle alternant entre mosaïque et plein écran
    /// </summary>
    private void StartCycleTimer()
    {
        // Commencer par afficher la mosaïque
        _cycleTimer = new System.Timers.Timer(MosaicDisplayDuration);
        _cycleTimer.Elapsed += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() => ToggleCycle());
        };
        _cycleTimer.Start();
    }

    /// <summary>
    /// Bascule entre mosaïque et plein écran
    /// </summary>
    private void ToggleCycle()
    {
        if (_isShowingFullscreen)
        {
            // On est en plein écran → fermer et revenir à la mosaïque
            HideFullscreen();

            // Reprogrammer le timer pour la durée de la mosaïque
            _cycleTimer?.Stop();
            _cycleTimer = new System.Timers.Timer(MosaicDisplayDuration);
            _cycleTimer.Elapsed += (s, e) => MainThread.BeginInvokeOnMainThread(() => ToggleCycle());
            _cycleTimer.Start();

            Console.WriteLine("📋 Retour à la mosaïque");
        }
        else
        {
            // On est en mosaïque → afficher une image en plein écran
            ShowNextFullscreen();

            // Reprogrammer le timer pour la durée du plein écran
            _cycleTimer?.Stop();
            _cycleTimer = new System.Timers.Timer(FullscreenDisplayDuration);
            _cycleTimer.Elapsed += (s, e) => MainThread.BeginInvokeOnMainThread(() => ToggleCycle());
            _cycleTimer.Start();

            Console.WriteLine("🖼️ Affichage plein écran");
        }
    }

    /// <summary>
    /// Affiche une image en plein écran - ATTEND que l'image soit préchargée
    /// </summary>
    private async void ShowNextFullscreen()
    {
        if (!_images.Any())
            return;

        _isShowingFullscreen = true;

        // Sélectionner une image aléatoire
        _currentFullscreenIndex = _random.Next(_images.Count);
        var currentImage = _images[_currentFullscreenIndex];
        currentImage.IsFullScreen = true;

        // CORRECTION : Utiliser l'image préchargée puis la réinitialiser
        string? fullscreenPath = _nextFullscreenImagePath;

        // Si pas préchargée, charger maintenant
        if (string.IsNullOrEmpty(fullscreenPath))
        {
            Console.WriteLine("⏳ Image plein écran non préchargée, chargement...");
            fullscreenPath = await _cacheService.GetFullSizeImagePathAsync(currentImage.NetworkPath);
        }
        else
        {
            // Réinitialiser car on l'a utilisée
            _nextFullscreenImagePath = null;
        }

        if (!string.IsNullOrEmpty(fullscreenPath))
        {
            currentImage.FullscreenPath = fullscreenPath;
            Console.WriteLine($"✅ Affichage plein écran: {Path.GetFileName(currentImage.NetworkPath)}");

            OnFullScreenChanged?.Invoke(_currentFullscreenIndex);
        }
        else
        {
            Console.WriteLine($"❌ Impossible de charger l'image plein écran");
            currentImage.IsFullScreen = false;
            _isShowingFullscreen = false;
        }

        // Précharger la prochaine image pour le prochain cycle
        _ = PreloadNextFullscreenImageAsync();
    }

    /// <summary>
    /// Ferme l'image plein écran et revient à la mosaïque
    /// </summary>
    private void HideFullscreen()
    {
        if (_currentFullscreenIndex >= 0 && _currentFullscreenIndex < _images.Count)
        {
            var oldImage = _images[_currentFullscreenIndex];
            oldImage.IsFullScreen = false;
            oldImage.FullscreenPath = null;

            Console.WriteLine($"🗑️ Fermeture plein écran: {Path.GetFileName(oldImage.NetworkPath)}");

            // Remplacer par une image aléatoire non encore affichée
            ReplaceImageWithNewOne(_currentFullscreenIndex);
        }

        _isShowingFullscreen = false;
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

            if (!string.IsNullOrEmpty(_nextFullscreenImagePath))
            {
                Console.WriteLine($"✅ Préchargement réussi: {Path.GetFileName(nextImage.NetworkPath)}");
            }
            else
            {
                Console.WriteLine($"⚠️ Préchargement échoué pour {Path.GetFileName(nextImage.NetworkPath)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur préchargement: {ex.Message}");
            _nextFullscreenImagePath = null;
        }
    }

    private async void ReplaceImageWithNewOne(int indexToReplace)
    {
        try
        {
            // Utiliser GetRandomUnusedImages pour éviter les répétitions
            var newImages = GetRandomUnusedImages(1);

            if (!newImages.Any())
            {
                Console.WriteLine("⚠️ Aucune nouvelle image disponible");
                return;
            }

            var newPath = newImages[0];

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
        _cycleTimer?.Dispose();
        _loadingTimer?.Dispose();
        _loadingSemaphore?.Dispose();
    }
}