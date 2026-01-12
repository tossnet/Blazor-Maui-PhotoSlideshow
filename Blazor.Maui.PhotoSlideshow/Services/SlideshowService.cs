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
    private readonly SemaphoreSlim _cycleLock = new(1, 1); // Verrou pour empêcher les cycles concurrents
    private string? _nextFullscreenImagePath; // Préchargement
    private bool _isAnimationRunning = false;
    private bool _isShowingFullscreen = false;
    private bool _isAnimationPaused = false;
    private bool _isCycleInProgress = false; // Empêche les cycles simultanés
    private HashSet<string> _displayedImages = new(); // Track des images déjà affichées
    private DateTime _animationStartTime; // Pour calculer la position de l'animation
    private double _animationPausedProgress = 0; // Sauvegarde de la progression quand pausé

    // AUGMENTÉ : Afficher beaucoup de miniatures pour créer un grand mur
    private const int MAX_VISIBLE_IMAGES = 150; // Grand mur de miniatures
    private const int INITIAL_RANDOM_COUNT = 30; // Nombre d'images aléatoires au démarrage
    private const double ANIMATION_DURATION_SECONDS = 50.0; // Durée du cycle d'animation CSS (50s)

    public event Action? OnImagesChanged;
    public event Action<int>? OnFullScreenChanged;
    
    /// <summary>
    /// Callback pour obtenir l'index de l'image centrale depuis JavaScript
    /// </summary>
    public Func<Task<int>>? GetCenterImageIndexCallback { get; set; }

    public List<ImageItem> Images => _images;
    public int MosaicDisplayDuration { get; set; } = 5000; // 5 secondes de mosaïque
    public int FullscreenDisplayDuration { get; set; } = 3000; // 3 secondes en plein écran
    public int TotalImages => _allNetworkImages.Count;
    public int LoadedImages => _lastProcessedDiscoveredCount;
    public bool IsLoadingComplete => _isLoadingComplete;
    public bool IsAnimationRunning => _isAnimationRunning;
    public bool IsAnimationPaused => _isAnimationPaused;
    public double AnimationProgress => _isAnimationPaused ? _animationPausedProgress : GetCurrentAnimationProgress();

    public SlideshowService(ImageCacheService cacheService)
    {
        _cacheService = cacheService;
        _cacheService.OnImagesDiscovered += OnNetworkImagesDiscovered;
    }

    /// <summary>
    /// Calcule la progression actuelle de l'animation CSS (0 à 1)
    /// </summary>
    private double GetCurrentAnimationProgress()
    {
        var elapsedSeconds = (DateTime.Now - _animationStartTime).TotalSeconds % ANIMATION_DURATION_SECONDS;
        return elapsedSeconds / ANIMATION_DURATION_SECONDS;
    }

    public async Task InitializeAsync()
    {
        // Charger immédiatement les miniatures déjà en cache
        await LoadCachedImagesImmediatelyAsync();

        // Puis démarrer le chargement en arrière-plan
        _cacheService.StartLoadingImagesInBackground();
        StartProgressiveLoading();
    }

    /// <summary>
    /// Charge immédiatement les miniatures déjà présentes dans le cache local
    /// pour un affichage instantané au démarrage
    /// </summary>
    private async Task LoadCachedImagesImmediatelyAsync()
    {
        var cachedThumbnails = _cacheService.GetCachedThumbnails(INITIAL_RANDOM_COUNT);

        if (!cachedThumbnails.Any())
        {
            Console.WriteLine("📭 Pas de miniatures en cache");
            return;
        }

        Console.WriteLine($"🚀 Affichage immédiat de {cachedThumbnails.Count} miniatures en cache");

        foreach (var thumbnailPath in cachedThumbnails)
        {
            var item = new ImageItem
            {
                NetworkPath = thumbnailPath,
                CachedPath = thumbnailPath,
                Opacity = 1.0
            };

            _images.Add(item);
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            OnImagesChanged?.Invoke();
        });
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
        _isAnimationPaused = false;
        _animationStartTime = DateTime.Now;

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
        _isAnimationPaused = false;
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
    private async void ToggleCycle()
    {
        // Éviter les cycles concurrents
        if (_isCycleInProgress)
        {
            Console.WriteLine("⚠️ Cycle déjà en cours, ignoré");
            return;
        }

        _isCycleInProgress = true;
        _cycleTimer?.Stop();

        try
        {
            if (_isShowingFullscreen)
            {
                // On est en plein écran → fermer et revenir à la mosaïque
                HideFullscreen();

                // Reprendre l'animation CSS
                _isAnimationPaused = false;
                // Recalculer le temps de départ pour continuer l'animation là où elle était
                _animationStartTime = DateTime.Now - TimeSpan.FromSeconds(_animationPausedProgress * ANIMATION_DURATION_SECONDS);

                Console.WriteLine("📋 Retour à la mosaïque - Animation reprise");
                OnImagesChanged?.Invoke();

                // Attendre un peu pour voir la mosaïque avant le prochain cycle
                await Task.Delay(100);

                // Reprogrammer le timer pour la durée de la mosaïque
                _cycleTimer = new System.Timers.Timer(MosaicDisplayDuration);
                _cycleTimer.AutoReset = false; // Important: un seul déclenchement
                _cycleTimer.Elapsed += (s, e) => MainThread.BeginInvokeOnMainThread(() => ToggleCycle());
                _cycleTimer.Start();
            }
            else
            {
                // Sauvegarder la progression de l'animation avant de la mettre en pause
                _animationPausedProgress = GetCurrentAnimationProgress();

                // Mettre en pause l'animation CSS AVANT d'afficher le plein écran
                _isAnimationPaused = true;
                OnImagesChanged?.Invoke();

                // Attendre un court instant pour que l'UI se mette à jour
                await Task.Delay(50);

                // On est en mosaïque → afficher l'image centrale en plein écran
                bool success = await ShowNextFullscreenAsync();

                if (success)
                {
                    Console.WriteLine("🖼️ Affichage plein écran - Animation en pause");

                    // Reprogrammer le timer pour la durée du plein écran
                    _cycleTimer = new System.Timers.Timer(FullscreenDisplayDuration);
                    _cycleTimer.AutoReset = false; // Important: un seul déclenchement
                    _cycleTimer.Elapsed += (s, e) => MainThread.BeginInvokeOnMainThread(() => ToggleCycle());
                    _cycleTimer.Start();
                }
                else
                {
                    // Échec du chargement, reprendre l'animation
                    Console.WriteLine("❌ Échec plein écran, reprise de la mosaïque");
                    _isAnimationPaused = false;
                    _animationStartTime = DateTime.Now - TimeSpan.FromSeconds(_animationPausedProgress * ANIMATION_DURATION_SECONDS);
                    OnImagesChanged?.Invoke();

                    // Réessayer après la durée de mosaïque
                    _cycleTimer = new System.Timers.Timer(MosaicDisplayDuration);
                    _cycleTimer.AutoReset = false;
                    _cycleTimer.Elapsed += (s, e) => MainThread.BeginInvokeOnMainThread(() => ToggleCycle());
                    _cycleTimer.Start();
                }
            }
        }
        finally
        {
            _isCycleInProgress = false;
        }
    }

    /// <summary>
    /// Obtient l'index de l'image centrale, en utilisant le callback JS si disponible,
    /// sinon utilise le calcul interne basé sur les keyframes CSS.
    /// </summary>
    private async Task<int> GetCenterImageIndexAsync()
    {
        if (!_images.Any())
            return -1;

        int detectedIndex = -1;

        // Essayer d'obtenir l'index via JavaScript (méthode précise)
        if (GetCenterImageIndexCallback != null)
        {
            try
            {
                detectedIndex = await GetCenterImageIndexCallback();
                Console.WriteLine($"🎯 Index central détecté par JS: {detectedIndex}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Erreur JS getCenterImageIndex: {ex.Message}");
                detectedIndex = -1;
            }
        }

        // Si JS a retourné un index valide, l'utiliser
        if (detectedIndex >= 0 && detectedIndex < _images.Count)
        {
            var selectedImage = _images[detectedIndex];
            
            // Si cette image a déjà été en plein écran, chercher la plus proche éligible
            if (selectedImage.HasBeenFullscreen)
            {
                var eligibleImages = _images
                    .Select((img, idx) => new { Image = img, Index = idx })
                    .Where(x => !x.Image.HasBeenFullscreen)
                    .ToList();

                if (!eligibleImages.Any())
                {
                    // Toutes affichées, réinitialiser
                    Console.WriteLine("🔄 Réinitialisation - toutes les images ont été affichées");
                    foreach (var img in _images)
                    {
                        img.HasBeenFullscreen = false;
                    }
                    return detectedIndex;
                }

                var closest = eligibleImages
                    .OrderBy(x => Math.Abs(x.Index - detectedIndex))
                    .First();
                
                Console.WriteLine($"🔄 Image {detectedIndex} déjà affichée, utilisation de {closest.Index}");
                return closest.Index;
            }

            return detectedIndex;
        }

        // Fallback: utiliser le calcul interne
        return GetCenterImageIndexFallback();
    }

    /// <summary>
    /// Calcul de fallback basé sur les keyframes CSS (utilisé si JS non disponible)
    /// </summary>
    private int GetCenterImageIndexFallback()
    {
        if (!_images.Any())
            return -1;

        var eligibleImages = _images
            .Select((img, idx) => new { Image = img, Index = idx })
            .Where(x => !x.Image.HasBeenFullscreen)
            .ToList();

        if (!eligibleImages.Any())
        {
            Console.WriteLine("🔄 Réinitialisation des images plein écran");
            foreach (var img in _images)
            {
                img.HasBeenFullscreen = false;
            }
            eligibleImages = _images
                .Select((img, idx) => new { Image = img, Index = idx })
                .ToList();
        }

        // Fallback simple: prendre une image aléatoire parmi les éligibles
        var randomIndex = _random.Next(eligibleImages.Count);
        return eligibleImages[randomIndex].Index;
    }

    /// <summary>
    /// Interpolation linéaire entre deux valeurs
    /// </summary>
    private static double Lerp(double a, double b, double t)
    {
        return a + (b - a) * Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// Affiche une image en plein écran - Attend que l'image soit chargée
    /// Retourne true si l'image a été chargée avec succès
    /// </summary>
    private async Task<bool> ShowNextFullscreenAsync()
    {
        if (!_images.Any())
            return false;

        _isShowingFullscreen = true;

        // Sélectionner l'image centrale via JS ou fallback
        _currentFullscreenIndex = await GetCenterImageIndexAsync();
        if (_currentFullscreenIndex < 0 || _currentFullscreenIndex >= _images.Count)
        {
            _currentFullscreenIndex = _random.Next(_images.Count);
        }

        var currentImage = _images[_currentFullscreenIndex];
        
        Console.WriteLine($"🎯 Image centrale sélectionnée (index {_currentFullscreenIndex}): {Path.GetFileName(currentImage.NetworkPath)}");

        // Charger l'image PLEIN ÉCRAN (pas la miniature)
        string? fullscreenPath = await _cacheService.GetFullSizeImagePathAsync(currentImage.NetworkPath);

        // Vérifier que ce n'est PAS une miniature
        if (!string.IsNullOrEmpty(fullscreenPath))
        {
            // Vérifier que le chemin n'est pas celui de la miniature
            if (fullscreenPath.Contains("_thumb") || fullscreenPath == currentImage.CachedPath)
            {
                Console.WriteLine($"⚠️ Chemin plein écran invalide (miniature détectée): {fullscreenPath}");
                _isShowingFullscreen = false;
                return false;
            }

            currentImage.IsFullScreen = true;
            currentImage.HasBeenFullscreen = true; // Marquer pour ne plus la choisir
            currentImage.FullscreenPath = fullscreenPath;
            
            Console.WriteLine($"✅ Affichage plein écran: {Path.GetFileName(currentImage.NetworkPath)}");

            OnFullScreenChanged?.Invoke(_currentFullscreenIndex);
            return true;
        }
        else
        {
            Console.WriteLine($"❌ Impossible de charger l'image plein écran");
            _isShowingFullscreen = false;
            return false;
        }
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
        _cycleLock?.Dispose();
    }
}