namespace Blazor.Maui.PhotoSlideshow.Models;

public class ImageItem
{
    public string NetworkPath { get; set; } = string.Empty;
    public string? CachedPath { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1.0;
    public bool IsFullScreen { get; set; }
}
