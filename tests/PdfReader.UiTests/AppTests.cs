using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfReader.App.Controls;
using PdfReader.App.ViewModels;
using PdfReader.Core.Settings;
using PdfReader.Rendering;
using PdfReader.Tests;

namespace PdfReader.UiTests;

/// <summary>Everything that is not a drawing tool: top bar, panels, menus, saving, settings, keyboard.</summary>
public class AppTests
{
    private const double Y = AppSession.TextY;

    private static async Task<AppSession> Start(string? pdf = null)
    {
        var app = await AppSession.StartAsync(pdf ?? AppSession.NewPdf());
        app.Key(Key.D0, RawInputModifiers.Control);
        await app.WaitSharpAsync();
        return app;
    }

    private static async Task Mark(AppSession app, double x1 = 76, double x2 = 300)
    {
        app.Key(Key.Home, RawInputModifiers.Control);
        await app.Settle();
        app.ChooseTool("Marker", "Highlight");
        await app.Drag(app.PagePoint(x1, Y), app.PagePoint(x2, Y));
        await AppSession.Until(() => app.Doc.CanUndo, "mark added");
        app.ChooseTool("Select", "Select");
    }

    private void ClickText(AppSession app, string text)
    {
        var tb = app.All<TextBlock>().First(t => t.Text == text && AppSession.IsShown(t));
        app.Click(tb);
    }

    // --- Top bar & All tools -----------------------------------------------------------------

    [AvaloniaFact]
    public async Task File_menu_has_all_items_and_they_work()
    {
        var app = await Start();
        var menu = (MenuFlyout)app.Button("File").Flyout!;
        var headers = AppSession.Flatten(menu.Items.OfType<MenuItem>()).Select(m => m.Header as string).ToList();
        foreach (var h in new[] { "Open…", "Open recent", "Save", "Save as…", "Print…", "Close", "Settings", "Exit" })
            Assert.Contains(h, headers);

        app.ChooseMenu(app.Button("File"), "Settings");
        Assert.True(app.Vm.IsSettingsOpen);
        app.Key(Key.Escape);
        app.ChooseMenu(app.Button("File"), "Close");
        await AppSession.Until(() => !app.Vm.HasDocuments, "document closed");
        Assert.True(app.HasText("Open PDF"));
        Assert.Same(app.Vm.PrintCommand, app.Button("Print").Command); // (the native print dialog can't open headless)
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task All_tools_panel_pushes_the_document_and_hides_again()
    {
        var app = await Start();
        var x0 = app.Center(app.Viewer).X;
        app.Click("All tools (Ctrl+T)");
        await AppSession.Until(() => app.Center(app.Viewer).X > x0 + 120, "document pushed right", 3000);
        var panel = app.All<PdfReader.App.Views.ToolsPanel>().First();
        Assert.True(panel.TranslatePoint(new Point(panel.Bounds.Width, 0), app.Window)!.Value.X
                    <= app.Viewer.TranslatePoint(new Point(0, 0), app.Window)!.Value.X + 1, "panel must not cover the page");
        app.Click("Hide");
        await AppSession.Until(() => Math.Abs(app.Center(app.Viewer).X - x0) < 2, "back", 3000);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task All_tools_rows_run_their_commands()
    {
        var app = await Start();
        async Task Run(string title)
        {
            app.Vm.IsToolsPanelOpen = true;
            var dock = app.All<Border>().First(b => b.Name == "ToolsDock");
            await AppSession.Until(() => dock.Bounds.Width > 299, "tools panel open", 3000);
            var row = app.All<Button>().First(b => b.Tag is AppCommand c && c.Title == title);
            row.BringIntoView(); // like scrolling the panel
            await app.Settle(60);
            app.Click(row);
            Dispatcher.UIThread.RunJobs();
        }
        await Run("Highlight"); Assert.Equal(ToolKind.Highlight, app.Markup.Tool);
        await Run("Draw"); Assert.Equal(ToolKind.Draw, app.Markup.Tool);
        await Run("Pan"); Assert.Equal(ToolKind.Pan, app.Markup.Tool);
        await Run("Fit page"); Assert.Equal(ZoomMode.FitPage, app.Doc.ZoomMode);
        await Run("Fit width"); Assert.Equal(ZoomMode.FitWidth, app.Doc.ZoomMode);
        await Run("Show bookmarks"); Assert.True(app.Doc.IsSidebarOpen && app.Doc.IsOutlineTab);
        await Run("Show comments"); Assert.True(app.Doc.IsCommentsTab);
        await Run("Show thumbnails"); Assert.True(app.Doc.IsThumbnailsTab);
        await Run("Cycle page colors"); Assert.Equal(PageColorMode.Dark, app.Vm.PageColorMode);
        await Run("Reading mode"); Assert.True(app.Vm.IsReadingMode); app.Key(Key.Escape); Assert.False(app.Vm.IsReadingMode);
        await Run("Command palette"); Assert.True(app.Vm.IsCommandPaletteOpen); app.Key(Key.Escape);
        await Run("Settings"); Assert.True(app.Vm.IsSettingsOpen); app.Key(Key.Escape);
        app.Window.Close();
    }

    // --- Toolbar ------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Toolbar_zoom_menu_and_undo_button()
    {
        var app = await Start();
        var zoom = app.All<Button>().First(b => AppSession.NameOf(b)?.EndsWith('%') == true);
        app.ChooseMenu(zoom, "200%"); await app.Settle(); Assert.Equal(2, app.Doc.Zoom, 2);
        app.ChooseMenu(zoom, "Actual size"); await app.Settle(); Assert.Equal(1, app.Doc.Zoom, 2);
        app.ChooseMenu(zoom, "Zoom in"); await app.Settle(); Assert.True(app.Doc.Zoom > 1);
        app.ChooseMenu(zoom, "Zoom out"); await app.Settle(); Assert.Equal(1, app.Doc.Zoom, 2);
        app.ChooseMenu(zoom, "Fit page"); await app.Settle(); Assert.Equal(ZoomMode.FitPage, app.Doc.ZoomMode);
        app.ChooseMenu(zoom, "Fit width"); await app.Settle(); Assert.Equal(ZoomMode.FitWidth, app.Doc.ZoomMode);
        Assert.False(app.Button("Undo").IsEnabled);
        await Mark(app);
        Assert.True(app.Button("Undo").IsEnabled);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Toolbar_can_be_moved_docked_and_resized()
    {
        var app = await Start();
        var bar = app.All<ToolbarView>().First();
        Point Grip() => app.Center(app.All<Border>().First(b => AppSession.NameOf(b)?.StartsWith("Drag to move") == true));
        var root = app.All<PdfReader.App.Views.DocumentView>().First();
        var right = root.TranslatePoint(new Point(root.Bounds.Width - 330, 300), app.Window)!.Value;
        var middle = root.TranslatePoint(new Point(root.Bounds.Width / 2, 300), app.Window)!.Value;
        var top = root.TranslatePoint(new Point(400, 6), app.Window)!.Value;
        var left = root.TranslatePoint(new Point(8, 300), app.Window)!.Value;

        await app.Drag(Grip(), middle); Assert.True(bar.Dock == ToolbarDock.Floating, $"middle: {bar.Dock}");
        await app.Drag(Grip(), right); Assert.True(bar.Dock == ToolbarDock.Right, $"right: {bar.Dock} grip={Grip()} drop={right}");
        await app.Drag(Grip(), top); Assert.True(bar.Dock == ToolbarDock.Top, $"top: {bar.Dock} grip={Grip()} drop={top} root={root.Bounds}");
        await app.Drag(Grip(), left); Assert.True(bar.Dock == ToolbarDock.Left, $"left: {bar.Dock}");
        Assert.Equal(ToolbarDock.Left, app.Vm.Settings.ToolbarDock);

        // Resize: narrow → icons only, wide → names again.
        var handle = app.All<Border>().First(b => AppSession.NameOf(b) == "Drag to resize" && b.Parent is Grid);
        var h = app.Center(handle);
        await app.Drag(h, new Point(h.X - 100, h.Y));
        Assert.True(app.Vm.Settings.ToolbarWidth < 110);
        Assert.DoesNotContain(app.All<TextBlock>(), t => t.Text == "Comment" && AppSession.IsShown(t) && t.FindAncestorOfType<ToolbarView>() is not null);
        h = app.Center(handle);
        await app.Drag(h, new Point(h.X + 100, h.Y));
        Assert.Contains(app.All<TextBlock>(), t => t.Text == "Comment" && AppSession.IsShown(t));
        app.Window.Close();
    }

    // --- Right panel ----------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Right_panel_pages_tab_navigates()
    {
        var app = await Start();
        app.Doc.ShowSidebar(SidebarTab.Thumbnails);
        await app.Settle();
        var box = app.Named<TextBox>("Go to page (Ctrl+G)");
        box.Text = "3";
        box.Focus();
        app.Key(Key.Enter);
        await AppSession.Until(() => app.Doc.CurrentPage == 2, "page 3");
        app.Click(app.All<Button>().First(b => b.DataContext is ThumbnailViewModel { PageIndex: 1 } && AppSession.IsShown(b)));
        await AppSession.Until(() => app.Doc.CurrentPage == 1, "page 2 via thumbnail");
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Right_panel_collapses_expands_switches_tabs_and_resizes()
    {
        var app = await Start();
        app.Doc.ShowSidebar(SidebarTab.Thumbnails);
        await app.Settle();
        var w0 = app.Viewer.Bounds.Width;
        app.Click("Hide panel");
        await app.Settle();
        Assert.False(app.Doc.IsSidebarOpen);
        Assert.True(app.Viewer.Bounds.Width > w0 + 200);
        app.Click(app.All<Button>().First(b => AppSession.IsShown(b) && ToolTip.GetTip(b) as string == "Bookmarks"));
        Assert.True(app.Doc.IsSidebarOpen && app.Doc.IsOutlineTab);
        app.Click(app.All<RadioButton>().First(r => AppSession.NameOf(r) == "Comments" && AppSession.IsShown(r)));
        Assert.True(app.Doc.IsCommentsTab);
        app.Click(app.All<RadioButton>().First(r => AppSession.NameOf(r) == "Pages" && AppSession.IsShown(r)));
        Assert.True(app.Doc.IsThumbnailsTab);

        var handle = app.All<Border>().First(b => b.Name == "ResizeHandle");
        var h = app.Center(handle);
        var before = app.Viewer.Bounds.Width;
        await app.Drag(h, new Point(h.X - 120, h.Y));
        Assert.True(app.Viewer.Bounds.Width < before - 80);
        Assert.True(app.Vm.Settings.RightPanelWidth > 380);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Bookmarks_go_add_delete_and_are_saved()
    {
        var pdf = AppSession.NewPdf();
        var app = await Start(pdf);
        app.Doc.ShowSidebar(SidebarTab.Outline);
        await AppSession.Until(() => app.Doc.Bookmarks.Count == 4, "bookmarks loaded");

        app.Click(app.All<Button>().First(b => b.DataContext is BookmarkItemViewModel { Title: "Chapter 3" } && AppSession.NameOf(b) == "Chapter 3"));
        await AppSession.Until(() => app.Doc.CurrentPage == 2, "went to chapter 3");

        app.Click("Add bookmark");
        await AppSession.Until(() => app.Vm.Dialog is not null, "name dialog");
        app.Vm.Dialog!.InputText = "My mark";
        app.Vm.Dialog.Primary();
        await AppSession.Until(() => app.Doc.Bookmarks.Any(b => b.Title == "My mark"), "bookmark added");

        app.Click(app.All<Button>().First(b => b.DataContext is BookmarkItemViewModel { Title: "Chapter 2" } && AppSession.NameOf(b) == "Delete bookmark"));
        Assert.DoesNotContain(app.Doc.Bookmarks, b => b.Title == "Chapter 2");
        Assert.True(app.Doc.IsModified);

        app.Key(Key.S, RawInputModifiers.Control);
        await AppSession.Until(() => !app.Doc.IsModified, "saved");
        app.Window.Close();

        await using var saved = await PdfDocument.OpenAsync(pdf);
        Assert.Equal(["Chapter 1", "Chapter 3", "Chapter 4", "My mark"], (await saved.GetOutlineAsync()).Select(o => o.Title));
    }

    [AvaloniaFact]
    public async Task Comments_list_goes_to_and_deletes_comments()
    {
        var app = await Start();
        app.ChooseTool("Comment", "Add comment");
        await app.Drag(app.PagePoint(76, Y), app.PagePoint(300, Y));
        app.Named<TextBox>("Write a comment…").Text = "Listed note";
        app.Click("Add comment");
        app.Key(Key.Escape);
        app.Doc.ShowSidebar(SidebarTab.Comments);
        await AppSession.Until(() => app.Doc.AllComments.Count == 1, "listed");
        var item = app.Doc.AllComments[0];
        Assert.Equal("Page 1", item.PageLabel);
        Assert.NotEqual("", item.DateText);

        app.Doc.GoToPage(3);
        await app.Settle();
        app.Click(app.All<Button>().First(b => b.DataContext == item && AppSession.NameOf(b) == "Listed note"));
        await AppSession.Until(() => app.Doc.CurrentPage == 0 && app.PopupTexts().Contains("Listed note") || app.HasText("Listed note") && app.HasButton("Delete"), "card shown");

        app.Click(app.All<Button>().First(b => b.DataContext == item && AppSession.NameOf(b) == "Delete comment"));
        await AppSession.Until(() => app.Doc.AllComments.Count == 0, "deleted");
        app.Window.Close();
    }

    // --- Right-click ------------------------------------------------------------------------------

    private static void RightClick(AppSession app, Point p)
    {
        app.Window.MouseMove(p);
        app.Window.MouseDown(p, MouseButton.Right);
        app.Window.MouseUp(p, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Right_click_recolors_edits_and_deletes()
    {
        var app = await Start();
        await Mark(app);
        RightClick(app, app.PagePoint(150, Y));
        await AppSession.Until(() => app.Viewer.LastContextMenu is not null, "context menu");
        var menu = app.Viewer.LastContextMenu!;
        AppSession.ChooseMenu(menu, "Blue");
        await AppSession.Until(() => (app.AnnotationAt(150, Y).Result!.Color & 0xFFFFFF) == 0x2F6FEB, "recolored blue");
        Assert.True(app.Doc.IsModified);

        RightClick(app, app.PagePoint(150, Y));
        await AppSession.Until(() => app.Viewer.LastContextMenu != menu, "second menu");
        AppSession.ChooseMenu(app.Viewer.LastContextMenu!, "Delete");
        await AppSession.Until(() => app.AnnotationAt(150, Y).Result is null, "deleted");

        // A comment can be edited from the menu.
        app.ChooseTool("Comment", "Add comment");
        await app.Drag(app.PagePoint(76, Y), app.PagePoint(300, Y));
        app.Named<TextBox>("Write a comment…").Text = "Before";
        app.Click("Add comment");
        app.ChooseTool("Select", "Select");
        var c = (await app.Doc.Document.GetCommentsAsync(0)).Single();
        menu = app.Viewer.LastContextMenu!;
        RightClick(app, app.PagePoint(c.Marker.X + c.Marker.Width / 2, c.Marker.Y + c.Marker.Height / 2));
        await AppSession.Until(() => app.Viewer.LastContextMenu != menu, "comment menu");
        AppSession.ChooseMenu(app.Viewer.LastContextMenu!, "Edit comment…");
        var box = app.All<TextBox>().First(t => t.Text == "Before");
        box.Text = "After";
        app.Click("Add comment");
        await AppSession.Until(() => app.Doc.Document.GetCommentsAsync(0).Result.Single().Text == "After", "comment edited");
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Right_click_on_selected_text_offers_actions()
    {
        var app = await Start();
        await app.Drag(app.PagePoint(76, Y), app.PagePoint(300, Y));
        await AppSession.Until(() => app.Doc.HasSelection, "selected");
        RightClick(app, app.PagePoint(150, Y));
        await AppSession.Until(() => app.Viewer.LastContextMenu is not null, "menu");
        var headers = AppSession.Flatten(app.Viewer.LastContextMenu!.Items.OfType<MenuItem>()).Select(m => m.Header as string).ToList();
        Assert.Equal(["Copy", "Highlight", "Underline", "Strikethrough", "Comment", "Read aloud"], headers);
        AppSession.ChooseMenu(app.Viewer.LastContextMenu!, "Underline");
        await AppSession.Until(() => app.AnnotationAt(150, Y).Result?.Kind == AnnotationKind.Underline, "underlined");
        app.Window.Close();
    }

    // --- Saving ----------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Save_save_as_and_unsaved_changes_prompt()
    {
        var pdf = AppSession.NewPdf();
        var size0 = new FileInfo(pdf).Length;
        var app = await Start(pdf);
        await Mark(app);
        Assert.StartsWith("●", app.Doc.TabTitle);

        app.Key(Key.S, RawInputModifiers.Control);
        await AppSession.Until(() => !app.Doc.IsModified, "saved");
        Assert.True(new FileInfo(pdf).Length > size0);

        var copy = Path.ChangeExtension(pdf, ".copy.pdf");
        app.Window.PickSavePathOverride = _ => copy;
        app.ChooseMenu(app.Button("File"), "Save as…");
        try { await AppSession.Until(() => app.Vm.ActiveDocument?.FilePath == copy || app.Vm.Dialog is not null, "switched to copy"); }
        catch (TimeoutException) { Assert.Fail($"active={app.Vm.ActiveDocument?.FilePath} docs={app.Vm.Documents.Count} copyExists={File.Exists(copy)} progress={app.Vm.ProgressText}"); }
        Assert.True(app.Vm.Dialog is null, app.Vm.Dialog?.Title + ": " + app.Vm.Dialog?.Message);
        await app.WaitSharpAsync();

        await Mark(app, 76, 150);
        app.Key(Key.W, RawInputModifiers.Control);
        await AppSession.Until(() => app.Vm.Dialog is { HasTertiary: true }, "save prompt");
        app.Vm.Dialog!.Tertiary(); // Cancel
        await app.Settle();
        Assert.True(app.Vm.HasDocuments);
        var len = new FileInfo(copy).Length;
        app.Key(Key.W, RawInputModifiers.Control);
        await AppSession.Until(() => app.Vm.Dialog is not null, "save prompt again");
        app.Vm.Dialog!.Secondary(); // Don't save
        await AppSession.Until(() => !app.Vm.HasDocuments, "closed");
        Assert.Equal(len, new FileInfo(copy).Length);
        app.Window.Close();
    }

    // --- Search, page colors, settings, palette, keyboard ----------------------------------------

    [AvaloniaFact]
    public async Task Search_box_buttons_and_options()
    {
        var app = await Start();
        var box = app.Named<TextBox>("Search in document");
        box.Text = "page";
        await AppSession.Until(() => app.Doc.SearchStatus.StartsWith("1 of 3"), "3 results");
        app.Click("Next result (F3)"); Assert.StartsWith("2 of", app.Doc.SearchStatus);
        app.Click("Previous result (Shift+F3)"); Assert.StartsWith("1 of", app.Doc.SearchStatus);
        app.Key(Key.F3); Assert.StartsWith("2 of", app.Doc.SearchStatus);
        app.Doc.MatchCase = true;
        box.Text = "PAGE";
        await AppSession.Until(() => app.Doc.SearchStatus == "No results", "match case");
        app.Doc.MatchCase = false;
        app.Doc.WholeWord = true;
        box.Text = "pag";
        await AppSession.Until(() => app.Doc.SearchStatus == "No results", "whole word");
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Page_colors_change_the_rendered_page()
    {
        var app = await Start();
        Assert.Equal(0xFFFFFFu, app.PixelAt(app.PagePoint(500, 400)) & 0xFFFFFF);
        app.Vm.PageModeDark = true;
        await app.WaitSharpAsync();
        await app.Settle(300);
        var dark = app.PixelAt(app.PagePoint(500, 400));
        Assert.True(((dark >> 16) & 0xFF) < 60, $"dark page expected, got {dark:X8}");
        app.Key(Key.D, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(PageColorMode.Sepia, app.Vm.PageColorMode);
        app.Vm.PageModeNormal = true;
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_pages_and_controls()
    {
        var app = await Start();
        app.Click("Settings (Ctrl+,)");
        Assert.True(app.Vm.IsSettingsOpen);
        ClickText(app, "Appearance");
        ClickText(app, "Light"); Assert.Equal(AppTheme.Light, app.Vm.Settings.Theme);
        ClickText(app, "Use Windows setting"); Assert.Equal(AppTheme.System, app.Vm.Settings.Theme);
        ClickText(app, "Sepia"); Assert.Equal(PageColorMode.Sepia, app.Vm.Settings.PageColorMode);
        ClickText(app, "Normal");
        ClickText(app, "Updates");
        app.Click(app.All<Button>().First(b => b.Content as string == "Check for updates" && AppSession.IsShown(b)));
        await AppSession.Until(() => app.HasText("Updates work in the installed or portable app, not in a development build."), "dev build message");
        ClickText(app, "Beta: try new features early"); Assert.Equal(UpdateChannel.Beta, app.Vm.Settings.UpdateChannel);
        ClickText(app, "Stable (recommended)"); Assert.Equal(UpdateChannel.Stable, app.Vm.Settings.UpdateChannel);
        ClickText(app, "General");
        var restore = app.Vm.Settings.RestoreSession;
        app.Vm.SettingsPage.RestoreSession = !restore;
        Assert.NotEqual(restore, app.Vm.Settings.RestoreSession);
        app.Click(app.All<Button>().First(b => AppSession.NameOf(b) == "Close" && AppSession.IsShown(b) && b.FindAncestorOfType<PdfReader.App.Views.SettingsView>() is not null));
        Assert.False(app.Vm.IsSettingsOpen);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Developer_mode_self_diagnosis_runs_and_reports()
    {
        var app = await Start();
        app.Click("Settings (Ctrl+,)");
        Assert.False(app.HasButton("Run self-diagnosis"));
        app.Vm.SettingsPage.DeveloperMode = true;
        await app.Settle();
        Assert.True(app.Vm.Settings.DeveloperMode);
        app.Click(app.All<Button>().First(b => b.Content as string == "Run self-diagnosis" && AppSession.IsShown(b)));
        Assert.False(app.Vm.IsSettingsOpen);

        // Headless windows only draw frames when told to; tick like a 60 Hz screen.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (app.Vm.Dialog is null && sw.Elapsed < TimeSpan.FromMinutes(3))
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(16);
        }
        var report = app.Vm.LastDiagnosticsReport;
        Assert.NotNull(report);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pdfreader-diagnosis-headless.txt"), report);
        var savedTo = app.Vm.Dialog!.Message.Split(Environment.NewLine)[^1];
        Assert.True(File.Exists(savedTo));
        File.Delete(savedTo);

        foreach (var section in new[] { "Environment", "PDF engine", "Annotations", "Tools & commands", "Live viewer (lag)", "Measurements" })
            Assert.Contains(section + Environment.NewLine, report);
        Assert.DoesNotContain("Section completed", report);
        // Frame timings are meaningless in a headless window; everything else must pass.
        var failures = report.Split(Environment.NewLine).Where(l => l.Contains("[FAIL]") && !l.Contains(": smooth") && !l.Contains("until sharp") && !l.Contains("Jump to")).ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        Assert.Single(app.Vm.Documents);
        Assert.DoesNotContain(app.Vm.RecentFiles, r => r.Path.Contains("pdfreader-diag", StringComparison.Ordinal));
        Assert.Equal(ToolKind.Select, app.Markup.Tool);
        app.Click(app.All<Button>().First(b => b.Content as string == "Close" && AppSession.IsShown(b) && b.FindAncestorOfType<PdfReader.App.Views.DialogView>() is not null));
        Assert.Null(app.Vm.Dialog);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Command_palette_runs_commands()
    {
        var app = await Start();
        app.Key(Key.K, RawInputModifiers.Control);
        Assert.True(app.Vm.IsCommandPaletteOpen);
        app.Vm.Palette.Query = "fit page";
        Assert.Equal("Fit page", app.Vm.Palette.SelectedCommand?.Title);
        app.Vm.Palette.Run();
        await app.Settle();
        Assert.False(app.Vm.IsCommandPaletteOpen);
        Assert.Equal(ZoomMode.FitPage, app.Doc.ZoomMode);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Keyboard_shortcuts()
    {
        var app = await Start();
        app.Key(Key.D1, RawInputModifiers.Control); await app.Settle(); Assert.Equal(1, app.Doc.Zoom, 2);
        app.Key(Key.OemPlus, RawInputModifiers.Control); await app.Settle(); Assert.True(app.Doc.Zoom > 1);
        app.Key(Key.D2, RawInputModifiers.Control); await app.Settle(); Assert.Equal(ZoomMode.FitPage, app.Doc.ZoomMode);
        app.Key(Key.End, RawInputModifiers.Control); await AppSession.Until(() => app.Doc.CurrentPage == 3, "last page");
        app.Key(Key.Home, RawInputModifiers.Control); await AppSession.Until(() => app.Doc.CurrentPage == 0, "first page");
        var open = app.Doc.IsSidebarOpen;
        app.Key(Key.F4); Assert.NotEqual(open, app.Doc.IsSidebarOpen);
        app.Key(Key.F11); Assert.True(app.Vm.IsReadingMode);
        app.Key(Key.Escape); Assert.False(app.Vm.IsReadingMode);
        app.Key(Key.G, RawInputModifiers.Control);
        await AppSession.Until(() => app.Vm.Dialog is not null, "go to page");
        app.Vm.Dialog!.InputText = "3"; app.Vm.Dialog.Primary();
        await AppSession.Until(() => app.Doc.CurrentPage == 2, "page 3");
        app.Key(Key.H, RawInputModifiers.Control | RawInputModifiers.Shift); Assert.Equal(ToolKind.Highlight, app.Markup.Tool);
        app.Key(Key.Escape); Assert.Equal(ToolKind.Select, app.Markup.Tool);
        app.Key(Key.T, RawInputModifiers.Control); Assert.True(app.Vm.IsToolsPanelOpen);
        app.Key(Key.T, RawInputModifiers.Control); Assert.False(app.Vm.IsToolsPanelOpen);
        app.Window.Close();
    }

    // --- Security, tabs, start screen --------------------------------------------------------------

    [AvaloniaFact]
    public async Task Active_content_is_reported_and_links_are_never_followed_silently()
    {
        var app = await Start(AppSession.NewPdf(new TestPdf.Options { Pages = ["Danger"], JavaScript = true, LaunchLink = true }));
        await AppSession.Until(() => app.Doc.IsSecurityBannerVisible && app.Doc.SecurityMessage.Contains("programs"), "banner");
        app.Click(app.PagePoint(100, 80));
        await AppSession.Until(() => app.Vm.Dialog?.Title == "Blocked", "launch blocked");
        app.Vm.Dialog!.Primary();
        app.Click("Dismiss");
        Assert.False(app.Doc.IsSecurityBannerVisible);

        await app.Vm.OpenFileAsync(AppSession.NewPdf(new TestPdf.Options { Pages = ["Web"], UriLink = true }));
        await app.WaitSharpAsync();
        app.Click(app.PagePoint(100, 80));
        await AppSession.Until(() => app.Vm.Dialog?.Title == "Open link?", "asked first");
        Assert.Contains("https://example.com/", app.Vm.Dialog!.Message);
        app.Vm.Dialog.Secondary(); // Cancel: nothing opens
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Tabs_switch_and_close_and_start_screen_reopens_recent()
    {
        var first = AppSession.NewPdf();
        var app = await Start(first);
        await app.Vm.OpenFileAsync(AppSession.NewPdf());
        await app.WaitSharpAsync();
        Assert.Equal(2, app.Vm.Documents.Count);
        app.Key(Key.Tab, RawInputModifiers.Control);
        Assert.Equal(first, app.Vm.ActiveDocument!.FilePath);
        var tab = app.All<ListBoxItem>().First(i => i.DataContext is DocumentViewModel d && d.FilePath != first);
        app.Click(tab.GetVisualDescendants().OfType<Button>().First());
        await AppSession.Until(() => app.Vm.Documents.Count == 1, "tab closed");
        app.Click("Close (Ctrl+W)");
        await AppSession.Until(() => !app.Vm.HasDocuments, "all closed");

        Assert.True(app.HasText("Open PDF"));
        app.Click(app.All<Button>().First(b => b.DataContext is RecentFileViewModel r && r.Path == first && b.Classes.Contains("row")));
        await AppSession.Until(() => app.Vm.ActiveDocument?.FilePath == first, "reopened from recent");
        app.Window.Close();
    }
}
