using Blazor.Maui.PhotoSlideshow.Services;
using Microsoft.AspNetCore.Components;
using Blazor.Maui.PhotoSlideshow.Models;

namespace Blazor.Maui.PhotoSlideshow.Components.Pages;

public partial class Home : IDisposable
{
    [Inject] private SlideshowService SlideshowService { get; set; } = default!;
    [Inject] private ImageCacheService ImageCacheService { get; set; } = default!;
    [Inject] private ImageConverterService ImageConverter { get; set; } = default!;

    private bool _isRunning = false;
    private bool _showSettings = false;
    private string _networkFolderInput = string.Empty;
    private List<ImageItem> _currentImages = new();
    private Dictionary<string, string> _imageSourceCache = new();
    private string? _fullscreenImageBase64 = null;
    private bool _isFullscreenExiting = false;

    // Compteur pour debouncing des rafraîchissements UI
    private int _imageUpdateCounter = 0;
    private const int UI_UPDATE_BATCH_SIZE = 20;
    private (int count, int pending) _cacheStats = (0, 0);

    protected override async Task OnInitializedAsync()
    {
        _networkFolderInput = ImageCacheService.NetworkFolder;

        SlideshowService.OnImagesChanged += OnImagesChanged;
        SlideshowService.OnFullScreenChanged += OnFullScreenChanged;

        await SlideshowService.InitializeAsync();
        UpdateImageSnapshot();

        // Précharger les premières images visibles en arrière-plan
        _ = PreloadVisibleImagesAsync();

        // Démarrer l'animation automatiquement après un court délai
        await Task.Delay(1000);
        if (_currentImages.Any() && !_isRunning)
        {
            ToggleAnimation();
        }

        // Timer pour mettre à jour les stats du cache
        _ = UpdateCacheStatsLoop();
    }

    /// <summary>
    /// Obtenir la source de l'image (Base64) avec chargement progressif
    /// </summary>
    private string GetImageSource(string imagePath)
    {
        // Si déjà en cache local, retourner immédiatement
        if (_imageSourceCache.TryGetValue(imagePath, out var source))
            return source;

        // Retourner un placeholder et charger en arrière-plan
        _ = LoadImageSourceAsync(imagePath);

        return "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='100' height='100'%3E%3Crect fill='%232a2a2a' width='100' height='100'/%3E%3C/svg%3E";
    }

    /// <summary>
    /// Charger l'image de manière asynchrone avec cache local
    /// </summary>
    private async Task LoadImageSourceAsync(string imagePath)
    {
        try
        {
            var source = await ImageConverter.ConvertToBase64Async(imagePath);
            _imageSourceCache[imagePath] = source;

            // Rafraîchir l'UI uniquement si nécessaire
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur chargement image: {ex.Message}");
        }
    }

    /// <summary>
    /// Précharger uniquement les images visibles
    /// </summary>
    private async Task PreloadVisibleImagesAsync()
    {
        // Limiter le préchargement aux 50 premières images pour éviter la surcharge
        var random = new Random();
        var imagesToPreload = _currentImages
            .Take(50)
            .OrderBy(_ => random.Next())
            .Where(img => !string.IsNullOrEmpty(img.CachedPath))
            .Select(img => img.CachedPath!)
            .ToList();

        // Précharger par lots de 10 pour ne pas surcharger
        foreach (var batch in imagesToPreload.Chunk(10))
        {
            var tasks = batch.Select(path => LoadImageSourceAsync(path));
            await Task.WhenAll(tasks);
            await Task.Delay(100); // Petit délai entre les lots
        }
    }

    /// <summary>
    /// Mise à jour périodique des stats du cache
    /// </summary>
    private async Task UpdateCacheStatsLoop()
    {
        while (!_isDisposed)
        {
            _cacheStats = ImageConverter.GetCacheStats();
            await Task.Delay(2000);
        }
    }

    private bool _isDisposed = false;

    /// <summary>
    /// Debouncing - rafraîchir UI tous les 20 images au lieu de chaque fois
    /// </summary>
    private void OnImagesChanged()
    {
        _imageUpdateCounter++;

        // Rafraîchir uniquement tous les N images OU quand le chargement est terminé
        if (_imageUpdateCounter % UI_UPDATE_BATCH_SIZE == 0 || SlideshowService.IsLoadingComplete)
        {
            InvokeAsync(() =>
            {
                UpdateImageSnapshot();

                // Réinitialiser l'image plein écran si fermée
                var hasFullscreen = _currentImages.Any(img => img.IsFullScreen);
                if (!hasFullscreen)
                {
                    _fullscreenImageBase64 = null;
                }

                StateHasChanged();

                // Précharger les nouvelles images
                _ = PreloadVisibleImagesAsync();
            });
        }
        else
        {
            // Mise à jour silencieuse sans StateHasChanged()
            UpdateImageSnapshot();
        }
    }

    private void OnFullScreenChanged(int index)
    {
        InvokeAsync(async () =>
        {
            // NOUVEAU: Si une image plein écran existe déjà, déclencher l'animation de sortie
        var previousFullscreenImage = _currentImages.FirstOrDefault(img => img.IsFullScreen);
        if (previousFullscreenImage != null && _fullscreenImageBase64 != null)
        {
            // Ajouter une classe CSS pour l'animation de sortie
            _isFullscreenExiting = true;
            StateHasChanged();
            
            // Attendre la fin de l'animation de sortie
            await Task.Delay(800); // Durée de l'animation CSS
        }

        _isFullscreenExiting = false;
            UpdateImageSnapshot();

            // Charger l'image plein écran AVANT de rafraîchir l'UI
            var fullscreenImage = _currentImages.FirstOrDefault(img => img.IsFullScreen);
            if (fullscreenImage?.FullscreenPath != null)
            {
                Console.WriteLine($"🔄 Chargement image plein écran: {Path.GetFileName(fullscreenImage.FullscreenPath)}");

                _fullscreenImageBase64 = null; // Réinitialiser
                //StateHasChanged(); // Afficher "Chargement..."

                // Charger l'image
                _fullscreenImageBase64 = await ImageConverter.ConvertToBase64Async(fullscreenImage.FullscreenPath);

                if (string.IsNullOrEmpty(_fullscreenImageBase64))
                {
                    Console.WriteLine($"❌ Échec chargement image plein écran");
                }
                else
                {
                    Console.WriteLine($"✅ Image plein écran chargée ({_fullscreenImageBase64.Length} caractères)");
                }
            }
            else
            {
                _fullscreenImageBase64 = null;
            }

            StateHasChanged();
        });
    }

    private void UpdateImageSnapshot()
    {
        _currentImages = SlideshowService.Images.ToList();
    }

    private void ToggleAnimation()
    {
        if (_isRunning)
        {
            SlideshowService.StopAnimation();
        }
        else
        {
            SlideshowService.StartAnimation();
        }
        _isRunning = !_isRunning;
    }

    private void ToggleSettings()
    {
        _showSettings = !_showSettings;
        if (_showSettings)
        {
            _networkFolderInput = ImageCacheService.NetworkFolder;
        }
    }

    private async Task SaveSettings()
    {
        if (!string.IsNullOrWhiteSpace(_networkFolderInput))
        {
            ImageCacheService.NetworkFolder = _networkFolderInput;
            _showSettings = false;
            await ClearCacheAndReload();
        }
    }

    private async Task ClearCacheAndReload()
    {
        SlideshowService.StopAnimation();
        _isRunning = false;
        ImageConverter.ClearCache();
        _imageSourceCache.Clear();
        _fullscreenImageBase64 = null;
        _imageUpdateCounter = 0;

        await SlideshowService.InitializeAsync();
        UpdateImageSnapshot();
        StateHasChanged();
    }

    public void Dispose()
    {
        _isDisposed = true;
        SlideshowService.OnImagesChanged -= OnImagesChanged;
        SlideshowService.OnFullScreenChanged -= OnFullScreenChanged;
        SlideshowService.StopAnimation();
        _imageSourceCache.Clear();
        _fullscreenImageBase64 = null;
        ImageConverter.ClearCache();
    }
}