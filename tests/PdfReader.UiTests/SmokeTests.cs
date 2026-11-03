using Avalonia.Headless.XUnit;

namespace PdfReader.UiTests;

public class SmokeTests
{
    [AvaloniaFact]
    public async Task Opens_and_renders_a_document()
    {
        var app = await AppSession.StartAsync(AppSession.NewPdf());
        Assert.Equal(4, app.Doc.PageCount);
        Assert.Equal(0xFFFFFFFFu, app.PixelAt(app.PagePoint(500, 400)) | 0xFF000000);
        app.Window.Close();
    }
}
