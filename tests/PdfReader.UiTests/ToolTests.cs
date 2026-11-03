using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PdfReader.App.ViewModels;
using PdfReader.Rendering;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PdfReader.UiTests;

/// <summary>Every tool on the toolbar, used with the mouse on a real page.</summary>
public class ToolTests
{
    private const double TextLeft = 76, TextRight = 300, Y = AppSession.TextY;

    private static async Task<AppSession> Start()
    {
        var app = await AppSession.StartAsync(AppSession.NewPdf());
        app.Doc.IsSidebarOpen = false; // more room; the panel has its own tests
        app.Key(Key.D0, RawInputModifiers.Control); // fit width
        await app.WaitSharpAsync();
        return app;
    }

    private static async Task AssertUndo(AppSession app, double x, double y)
    {
        app.Click("Undo");
        await AppSession.Until(() => !app.Doc.CanUndo, "undo");
        Assert.Null(await app.AnnotationAt(x, y));
    }

    [AvaloniaFact]
    public async Task Every_tool_in_every_group_can_be_chosen_from_the_toolbar()
    {
        var app = await Start();
        foreach (var group in app.Markup.Groups)
        {
            foreach (var tool in group.Items)
            {
                app.ChooseTool(group.Title, tool.Title);
                Assert.Equal(tool.Kind, app.Markup.Tool);
                Assert.True(app.Button(group.Title).Classes.Contains("active"), $"{group.Title} should be highlighted");
                Assert.Equal(tool.Kind != ToolKind.Select && tool.Kind != ToolKind.Pan, app.Markup.HasOptions);
            }
        }
        app.Key(Key.Escape);
        app.Key(Key.Escape);
        Assert.Equal(ToolKind.Select, app.Markup.Tool);
        app.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData("Highlight", AnnotationKind.Highlight)]
    [InlineData("Underline", AnnotationKind.Underline)]
    [InlineData("Strikethrough", AnnotationKind.StrikeOut)]
    public async Task Marker_tools_mark_text_and_undo(string tool, AnnotationKind kind)
    {
        var app = await Start();
        app.ChooseTool("Marker", tool);
        await app.Drag(app.PagePoint(TextLeft, Y), app.PagePoint(TextRight, Y));
        await AppSession.Until(() => app.Doc.CanUndo, "mark added");
        var hit = await app.AnnotationAt(150, Y);
        Assert.Equal(kind, hit?.Kind);
        Assert.True(app.Doc.IsModified);
        await AssertUndo(app, 150, Y);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Marker_options_change_color_transparency_and_strength()
    {
        var app = await Start();
        app.ChooseTool("Marker", "Highlight");
        app.Click(app.Button("#00E5FF"));
        app.Named<Slider>("Transparency").Value = 30;
        app.Named<Slider>("Color strength").Value = 60;
        Assert.Equal("#00E5FF", app.Vm.Settings.MarkerColor);
        Assert.Equal(0.3, app.Vm.Settings.MarkerTransparency, 2);
        Assert.Equal(0.6, app.Vm.Settings.MarkerStrength, 2);

        await app.Drag(app.PagePoint(TextLeft, Y), app.PagePoint(TextRight, Y));
        await AppSession.Until(() => app.Doc.CanUndo, "mark added");
        var hit = await app.AnnotationAt(150, Y);
        Assert.Equal(app.Vm.Settings.ResolveMarkerArgb() & 0xFFFFFF, hit!.Color & 0xFFFFFF);
        Assert.InRange((int)(hit.Color >> 24), (int)(app.Vm.Settings.ResolveMarkerArgb() >> 24) - 2, (int)(app.Vm.Settings.ResolveMarkerArgb() >> 24) + 2);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Highlight_on_an_image_area_draws_freehand()
    {
        var app = await Start();
        app.ChooseTool("Marker", "Highlight");
        await app.Drag(app.PagePoint(120, 300), app.PagePoint(320, 340));
        await AppSession.Until(() => app.Doc.CanUndo, "drawing added");
        Assert.Equal(AnnotationKind.Ink, (await app.AnnotationAt(220, 320))?.Kind);
        app.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData("Draw", AnnotationKind.Ink)]
    [InlineData("Line", AnnotationKind.Ink)]
    [InlineData("Line arrow", AnnotationKind.Ink)]
    [InlineData("Arrow", AnnotationKind.Stamp)]
    [InlineData("Rectangle", AnnotationKind.Square)]
    [InlineData("Circle", AnnotationKind.Circle)]
    [InlineData("Cloud", AnnotationKind.Ink)]
    public async Task Draw_tools_create_shapes_and_undo(string tool, AnnotationKind kind)
    {
        var app = await Start();
        app.ChooseTool("Draw", tool);
        app.Named<Slider>("Thickness").Value = 3;
        app.Click(app.Button("#2F6FEB"));
        await app.Drag(app.PagePoint(150, 300), app.PagePoint(350, 420));
        await AppSession.Until(() => app.Doc.CanUndo, $"{tool} added");
        var hit = await app.AnnotationAt(250, 360);
        Assert.Equal(kind, hit?.Kind);
        await AssertUndo(app, 250, 360);
        app.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData("Polygon")]
    [InlineData("Connected line")]
    public async Task Multi_click_tools_finish_with_double_click_or_enter(string tool)
    {
        var app = await Start();
        app.ChooseTool("Draw", tool);
        foreach (var (x, y) in new[] { (150.0, 300.0), (350.0, 310.0), (330.0, 420.0) })
        {
            app.Click(app.PagePoint(x, y));
            await app.Settle(60);
        }
        app.Viewer.Focus();
        app.Key(Key.Enter);
        await AppSession.Until(() => app.Doc.CanUndo, $"{tool} added");
        Assert.Equal(AnnotationKind.Ink, (await app.AnnotationAt(250, 330))?.Kind);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Text_callout_asks_for_text_then_places_a_box()
    {
        var app = await Start();
        app.ChooseTool("Draw", "Text callout");
        await app.Drag(app.PagePoint(150, 300), app.PagePoint(300, 420));
        var box = app.Named<TextBox>("Type text…");
        box.Text = "Callout note";
        app.Click("Add comment");
        await AppSession.Until(() => app.Doc.CanUndo, "callout added");
        Assert.Equal(AnnotationKind.Stamp, (await app.AnnotationAt(310, 430))?.Kind);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Stamp_palette_places_the_chosen_stamp()
    {
        var app = await Start();
        app.ChooseTool("Draw", "Stamp");
        app.Click("Confidential");
        Assert.Equal("StampConfidential", app.Vm.Settings.StampLabel);
        app.Click(app.PagePoint(300, 400));
        await AppSession.Until(() => app.Doc.CanUndo, "stamp added");
        var hit = await app.AnnotationAt(300, 400);
        Assert.Equal(AnnotationKind.Stamp, hit?.Kind);
        Assert.Equal("Confidential", hit!.Contents);
        app.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData("Crossmark")]
    [InlineData("Checkmark")]
    [InlineData("Dot")]
    public async Task Text_group_symbols_are_placed_by_clicking(string tool)
    {
        var app = await Start();
        app.ChooseTool("Text", tool);
        app.Click(app.PagePoint(300, 400));
        await AppSession.Until(() => app.Doc.CanUndo, $"{tool} added");
        Assert.Equal(AnnotationKind.Stamp, (await app.AnnotationAt(300, 400))?.Kind);
        await AssertUndo(app, 300, 400);
        app.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData("Circle around", AnnotationKind.Circle)]
    [InlineData("Cross out", AnnotationKind.Ink)]
    public async Task Text_group_drag_tools(string tool, AnnotationKind kind)
    {
        var app = await Start();
        app.ChooseTool("Text", tool);
        await app.Drag(app.PagePoint(150, 300), app.PagePoint(350, 420));
        await AppSession.Until(() => app.Doc.CanUndo, $"{tool} added");
        Assert.Equal(kind, (await app.AnnotationAt(250, 360))?.Kind);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Type_text_with_size()
    {
        var app = await Start();
        app.ChooseTool("Text", "Type text");
        app.Named<Slider>("Size").Value = 20;
        app.Click(app.PagePoint(150, 300));
        app.Named<TextBox>("Type text…").Text = "Hello there";
        app.Click("Add comment");
        await AppSession.Until(() => app.Doc.CanUndo, "text added");
        Assert.Equal(AnnotationKind.Stamp, (await app.AnnotationAt(170, 310))?.Kind);
        Assert.Equal(20, app.Vm.Settings.TextSize);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Comment_tools_add_comments_shown_in_the_comments_list()
    {
        var app = await Start();

        // Add comment: shape, color, last word; then hover shows the card.
        app.ChooseTool("Comment", "Add comment");
        app.Click(app.Button("Cloud"));
        app.Click(app.Button("#2F6FEB"));
        await app.Drag(app.PagePoint(TextLeft, Y), app.PagePoint(TextRight, Y));
        app.Named<TextBox>("Write a comment…").Text = "First note";
        app.Click("Add comment");
        await AppSession.Until(() => app.Doc.CanUndo, "comment added");
        var comments = await app.Doc.Document.GetCommentsAsync(0);
        var c = Assert.Single(comments);
        Assert.Equal(CommentShape.Cloud, c.Shape);
        Assert.Equal(0xFF2F6FEBu, c.Color);
        app.Window.MouseMove(app.PagePoint(c.Marker.X + c.Marker.Width / 2, c.Marker.Y + c.Marker.Height / 2));
        await app.Settle(150);
        Assert.True(app.HasText("First note"), "hover card shows the comment");

        // Replace selected text
        app.ChooseTool("Comment", "Replace selected text");
        await app.Drag(app.PagePoint(TextLeft, Y), app.PagePoint(150, Y));
        app.Named<TextBox>("Replace with…").Text = "A";
        app.Click("Add comment");

        // Insert text
        app.ChooseTool("Comment", "Insert text");
        app.Click(app.PagePoint(200, 200));
        app.Named<TextBox>("Text to insert…").Text = "inserted";
        app.Click("Add comment");

        // Text comment
        app.ChooseTool("Comment", "Add text comment");
        app.Click(app.PagePoint(200, 380));
        app.Named<TextBox>("Write a comment…").Text = "Boxed note";
        app.Click("Add comment");

        // Attach file (file picker replaced by the test)
        var file = Path.Combine(Path.GetTempPath(), $"attach-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "attachment");
        app.Window.PickFileOverride = () => file;
        app.ChooseTool("Comment", "Attach file");
        app.Click(app.PagePoint(450, 400));

        await AppSession.Until(() => app.Doc.Document.GetCommentsAsync(0).Result.Count == 5, "five comments", 10000);
        app.Doc.ShowSidebar(SidebarTab.Comments);
        await AppSession.Until(() => app.Doc.AllComments.Count == 5, "comments list", 10000);
        Assert.Contains(app.Doc.AllComments, i => i.Text == "Replace with: A");
        Assert.Contains(app.Doc.AllComments, i => i.Text == "Insert: inserted");
        Assert.Contains(app.Doc.AllComments, i => i.Text == "Boxed note");
        Assert.Contains(app.Doc.AllComments, i => i.Text.Contains(Path.GetFileName(file)));
        Assert.All(app.Doc.AllComments, i => Assert.Equal("Page 1", i.PageLabel));
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Signature_and_initials_drawn_or_typed_then_reused()
    {
        var app = await Start();
        app.ChooseTool("Signature", "Add signature");
        app.Click(app.PagePoint(300, 400));
        await AppSession.Until(() => app.Vm.SignatureDialog is not null, "signature pad");
        var pad = app.All<PdfReader.App.Controls.SignaturePad>().First();
        var c = app.Center(pad);
        await app.Drag(new Point(c.X - 120, c.Y + 20), new Point(c.X + 120, c.Y - 20));
        app.Click("Save & use");
        await AppSession.Until(() => app.Doc.CanUndo, "signature placed");
        Assert.Equal(AnnotationKind.Stamp, (await app.AnnotationAt(300, 400))?.Kind);
        Assert.NotNull(app.Vm.State.Signature);

        // Second time: no pad, placed straight away.
        app.Click(app.PagePoint(450, 250));
        await AppSession.Until(() => app.AnnotationAt(450, 250).Result is not null, "signature reused");
        Assert.Null(app.Vm.SignatureDialog);

        // Initials, typed
        app.ChooseTool("Signature", "Add initials");
        app.Click(app.PagePoint(150, 250));
        await AppSession.Until(() => app.Vm.SignatureDialog is not null, "initials pad");
        app.Vm.SignatureDialog!.IsTypeTab = true;
        app.Vm.SignatureDialog.TypedText = "JH";
        app.Click("Save & use");
        await AppSession.Until(() => app.AnnotationAt(150, 250).Result is not null, "initials placed");
        Assert.Equal("JH", app.Vm.State.Initials?.Typed);
        app.Window.Close();
    }

    [AvaloniaFact]
    public async Task Pan_tool_scrolls_the_page()
    {
        var app = await Start();
        app.ChooseTool("Select", "Pan");
        var before = app.Viewer.ScrollY;
        var c = app.Center(app.Viewer);
        await app.Drag(new Point(c.X, c.Y + 250), new Point(c.X, c.Y - 250));
        Assert.True(app.Viewer.ScrollY > before + 100, "dragging up scrolls down");
        app.Window.Close();
    }
}
