using System;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkSuite1.Services;
using SkiaSharp;

namespace BenchmarkSuite1;

[MemoryDiagnoser]
public class ImageProcessingBenchmarks
{
    private ThumbnailService _thumbnailService = null!;
    private ImageConverterService _imageConverter = null!;
    private string _testImagePath = null!;
    private string _testThumbnailPath = null!;
    private string _existingThumbnail = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _thumbnailService = new ThumbnailService();
        _imageConverter = new ImageConverterService();

        var tempDir = Path.Combine(Path.GetTempPath(), "PhotoSlideshowBenchmarks");
        Directory.CreateDirectory(tempDir);

        _testImagePath = Path.Combine(tempDir, "test_image.jpg");
        _testThumbnailPath = Path.Combine(tempDir, "test_thumbnail.jpg");

        if (!File.Exists(_testImagePath))
        {
            using var bitmap = new SKBitmap(4000, 3000);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Blue);
            
            using var paint = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 100,
                IsAntialias = true
            };
            canvas.DrawText("Benchmark Image", 100, 200, paint);

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            await File.WriteAllBytesAsync(_testImagePath, data.ToArray());
        }

        _existingThumbnail = await _thumbnailService.CreateThumbnailAsync(_testImagePath, _testThumbnailPath) ?? string.Empty;
    }

    [Benchmark]
    public async Task<string?> ThumbnailCreation()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid()}.jpg");
        var result = await _thumbnailService.CreateThumbnailAsync(_testImagePath, outputPath);
        
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        
        return result;
    }

    [Benchmark]
    public async Task<string> Base64Conversion()
    {
        _imageConverter.ClearCache();
        return await _imageConverter.ConvertToBase64Async(_existingThumbnail);
    }

    [Benchmark]
    public async Task<string> Base64ConversionWithCache()
    {
        await _imageConverter.ConvertToBase64Async(_existingThumbnail);
        return await _imageConverter.ConvertToBase64Async(_existingThumbnail);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _imageConverter.ClearCache();
        
        var tempDir = Path.Combine(Path.GetTempPath(), "PhotoSlideshowBenchmarks");
        if (Directory.Exists(tempDir))
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}