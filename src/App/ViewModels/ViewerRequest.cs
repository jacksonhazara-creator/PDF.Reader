using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.ViewModels;

/// <summary>Requests from view models to the viewer control, which owns scroll and zoom.</summary>
public abstract record ViewerRequest
{
    public sealed record GoToPage(int PageIndex, double Offset = 0) : ViewerRequest;
    public sealed record ZoomStep(int Direction) : ViewerRequest;
    public sealed record SetZoom(double Zoom) : ViewerRequest;
    public sealed record SetZoomMode(ZoomMode Mode) : ViewerRequest;
    public sealed record Reveal(int PageIndex, PdfRect Rect) : ViewerRequest;
    public sealed record ScrollByPage(int Direction) : ViewerRequest;
}
