using PdfReader.App;

namespace PdfReader.UiTests;

public class StartupOptionsTests
{
    [Fact]
    public void Parses_diagnose_switch_and_files()
    {
        var o = StartupOptions.Parse(["--diagnose", @"C:\r.txt", "a.pdf"]);
        Assert.Equal(@"C:\r.txt", o.DiagnoseOutput);
        Assert.Null(o.BenchmarkOutput);
        Assert.Equal(["a.pdf"], o.Files);
        Assert.True(o.IsAutomated);
    }

    [Fact]
    public void Plain_launch_is_not_automated()
    {
        var o = StartupOptions.Parse(["a.pdf"]);
        Assert.False(o.IsAutomated);
        Assert.Null(o.DiagnoseOutput);
    }
}
