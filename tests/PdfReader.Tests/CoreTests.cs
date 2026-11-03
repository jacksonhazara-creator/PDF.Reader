using PdfReader.Core.Commands;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.Tests;

public class CoreTests
{
    [Fact]
    public void Settings_round_trip_and_survive_corruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pdfreader-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonStore(dir);
            Assert.Equal(PageColorMode.Normal, store.LoadSettings().PageColorMode); // defaults when missing

            store.SaveSettings(new AppSettings { PageColorMode = PageColorMode.Sepia, Language = "de", UpdateChannel = UpdateChannel.Beta });
            var loaded = store.LoadSettings();
            Assert.Equal(PageColorMode.Sepia, loaded.PageColorMode);
            Assert.Equal("de", loaded.Language);
            Assert.Equal(UpdateChannel.Beta, loaded.UpdateChannel);
            Assert.Contains("\"sepia\"", File.ReadAllText(Path.Combine(dir, "settings.json")), StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
            Assert.Equal(PageColorMode.Normal, store.LoadSettings().PageColorMode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Recent_files_are_unique_newest_first_and_capped()
    {
        var state = new AppState();
        for (var i = 0; i < 30; i++)
            state.AddRecent($@"C:\docs\{i}.pdf", 1);
        state.AddRecent(@"C:\DOCS\5.pdf", 3);

        Assert.Equal(AppState.MaxRecentFiles, state.RecentFiles.Count);
        Assert.Equal(@"C:\DOCS\5.pdf", state.RecentFiles[0].Path);
        Assert.Single(state.RecentFiles, r => r.Path.Equals(@"C:\docs\5.pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Session_and_view_state_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pdfreader-state-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonStore(dir);
            var state = new AppState();
            state.Session.Add(new SessionTab { Path = @"C:\a.pdf" });
            var view = state.GetDocumentState(@"C:\a.pdf");
            view.PageIndex = 41;
            view.PageColorOverride = PageColorMode.Dark;
            store.SaveState(state);

            var loaded = store.LoadState();
            Assert.Equal(@"C:\a.pdf", loaded.Session.Single().Path);
            Assert.Equal(41, loaded.Documents[@"C:\a.pdf"].PageIndex);
            Assert.Equal(PageColorMode.Dark, loaded.Documents[@"C:\a.pdf"].PageColorOverride);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("#fff", 0xFFFFFFFFu)]
    [InlineData("#1E1E1E", 0xFF1E1E1Eu)]
    [InlineData("80102030", 0xFF102030u)]
    public void Parses_colors(string text, uint expected)
    {
        Assert.True(ColorParser.TryParse(text, out var c));
        Assert.Equal(expected, c);
    }

    [Fact]
    public void Custom_page_colors_come_from_settings()
    {
        var s = new AppSettings { PageColorMode = PageColorMode.Custom, CustomForeground = "#00FF00", CustomBackground = "#000080" };
        var colors = s.ResolvePageColors();
        Assert.Equal(0xFF00FF00u, colors.Foreground);
        Assert.Equal(0xFF000080u, colors.Background);
        Assert.Equal(PageColorMode.Dark, s.ResolvePageColors(PageColorMode.Dark).Mode);
    }

    [Theory]
    [InlineData("https://example.com/a?b=c", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("\\\\server\\share\\evil.exe", false)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    public void Only_web_and_mail_links_can_be_opened(string target, bool allowed)
    {
        Assert.Equal(allowed, PdfReader.Core.Security.LinkPolicy.IsAllowedUri(target, out _));
    }

    [Fact]
    public void Marker_color_combines_strength_and_transparency()
    {
        var s = new AppSettings { MarkerColor = "#FF0000", MarkerStrength = 1, MarkerTransparency = 0 };
        Assert.Equal(0xFFFF0000u, s.ResolveMarkerArgb());

        s.MarkerTransparency = 0.5;
        Assert.Equal(0x80u, s.ResolveMarkerArgb() >> 24);

        s.MarkerStrength = 0.5; // half way to white
        var rgb = s.ResolveMarkerArgb() & 0xFFFFFF;
        Assert.Equal(0xFF8080u, rgb, new ToleranceComparer(1));
    }

    private sealed class ToleranceComparer(int tolerance) : IEqualityComparer<uint>
    {
        public bool Equals(uint a, uint b) => Enumerable.Range(0, 3).All(i => Math.Abs((int)((a >> (i * 8)) & 0xFF) - (int)((b >> (i * 8)) & 0xFF)) <= tolerance);
        public int GetHashCode(uint obj) => 0;
    }

    [Fact]
    public void Command_matcher_ranks_prefix_over_fuzzy()
    {
        var prefix = CommandMatcher.Score("Page colors", "page");
        var word = CommandMatcher.Score("Toggle page colors", "page");
        var fuzzy = CommandMatcher.Score("Print", "prt");
        Assert.True(prefix > word);
        Assert.True(word > fuzzy);
        Assert.True(fuzzy > 0);
        Assert.Equal(0, CommandMatcher.Score("Zoom in", "xyz"));
    }
}
