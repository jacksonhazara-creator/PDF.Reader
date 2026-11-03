using System.Globalization;
using Avalonia.Markup.Xaml;

namespace PdfReader.App.Localization;

/// <summary>
/// English/German UI strings. A plain dictionary keeps this AOT-safe and fast to load;
/// the language is picked once at startup (changing it in Settings applies on restart).
/// </summary>
public static class Loc
{
    // Built on first use: static initializers run top to bottom, and Strings is declared below.
    private static Dictionary<string, string>? s_current;

    public static string Language { get; private set; } = "en";

    public static void Initialize(string setting)
    {
        var lang = setting switch
        {
            "en" or "de" => setting,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en",
        };
        Language = lang;
        s_current = Build(lang == "de" ? 1 : 0);
    }

    public static string Get(string key) => (s_current ??= Build(0)).TryGetValue(key, out var value) ? value : key;

    public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    private static Dictionary<string, string> Build(int column)
    {
        var dict = new Dictionary<string, string>(Strings.Length);
        foreach (var (key, en, de) in Strings)
            dict[key] = column == 1 ? de : en;
        return dict;
    }

    private static readonly (string Key, string En, string De)[] Strings =
    [
        // App / top bar
        ("AppName", "PDF.Reader", "PDF.Reader"),
        ("AllTools", "All tools", "Alle Werkzeuge"),
        ("AllToolsTip", "All tools (Ctrl+T)", "Alle Werkzeuge (Strg+T)"),
        ("SettingsTip", "Settings (Ctrl+,)", "Einstellungen (Strg+,)"),
        ("PageColorsTip", "Page colors (Ctrl+Shift+D)", "Seitenfarben (Strg+Umschalt+D)"),
        ("SearchPlaceholder", "Search in document", "Im Dokument suchen"),
        ("SearchTip", "Search (Ctrl+F). Enter: next, Shift+Enter: previous", "Suchen (Strg+F). Eingabe: weiter, Umschalt+Eingabe: zurück"),
        ("OpenTip", "Open a PDF (Ctrl+O)", "PDF öffnen (Strg+O)"),
        ("CloseTabTip", "Close (Ctrl+W)", "Schließen (Strg+W)"),
        ("NoResults", "No results", "Keine Treffer"),
        ("ResultOf", "{0} of {1}", "{0} von {1}"),
        ("Searching", "Searching…", "Suche läuft…"),
        ("MatchCase", "Match case", "Groß-/Kleinschreibung"),
        ("SearchOptions", "Search options", "Suchoptionen"),
        ("WholeWords", "Whole words", "Ganze Wörter"),
        ("PreviousResult", "Previous result (Shift+F3)", "Vorheriger Treffer (Umschalt+F3)"),
        ("NextResult", "Next result (F3)", "Nächster Treffer (F3)"),

        // Start screen
        ("OpenPdf", "Open PDF", "PDF öffnen"),
        ("DropHint", "or drop a PDF here", "oder PDF hierher ziehen"),
        ("RecentFiles", "Recent", "Zuletzt geöffnet"),
        ("NoRecentFiles", "Files you open will show up here.", "Hier erscheinen Dateien, die Sie öffnen."),
        ("RemoveFromRecent", "Remove from list", "Aus Liste entfernen"),
        ("Pages", "{0} pages", "{0} Seiten"),
        ("OnePage", "1 page", "1 Seite"),

        // Viewer
        ("PageOf", "of {0}", "von {0}"),
        ("PageNumberTip", "Go to page (Ctrl+G)", "Gehe zu Seite (Strg+G)"),
        ("ZoomIn", "Zoom in", "Vergrößern"),
        ("ZoomOut", "Zoom out", "Verkleinern"),
        ("ZoomInTip", "Zoom in (Ctrl+Plus)", "Vergrößern (Strg+Plus)"),
        ("ZoomOutTip", "Zoom out (Ctrl+Minus)", "Verkleinern (Strg+Minus)"),
        ("FitWidth", "Fit width", "Seitenbreite"),
        ("FitPage", "Fit page", "Ganze Seite"),
        ("ActualSize", "Actual size", "Originalgröße"),
        ("FitWidthTip", "Fit width (Ctrl+0)", "Seitenbreite (Strg+0)"),
        ("FitPageTip", "Fit page (Ctrl+2)", "Ganze Seite (Strg+2)"),
        ("Thumbnails", "Thumbnails", "Miniaturansichten"),
        ("Outline", "Bookmarks", "Lesezeichen"),
        ("NoOutline", "This document has no bookmarks.", "Dieses Dokument hat keine Lesezeichen."),
        ("SidebarTip", "Thumbnails and bookmarks (F4)", "Miniaturansichten und Lesezeichen (F4)"),
        ("Copy", "Copy", "Kopieren"),
        ("ReadAloud", "Read aloud", "Vorlesen"),
        ("Copied", "Copied", "Kopiert"),

        // Security
        ("SecurityBanner", "This PDF contains {0}. For your safety they are blocked.", "Diese PDF enthält {0}. Zu Ihrer Sicherheit sind diese blockiert."),
        ("SecScripts", "scripts", "Skripte"),
        ("SecAttachments", "attachments", "Anhänge"),
        ("SecLaunch", "actions that start programs", "Aktionen, die Programme starten"),
        ("And", "and", "und"),
        ("Dismiss", "Dismiss", "Ausblenden"),
        ("OpenLinkTitle", "Open link?", "Link öffnen?"),
        ("OpenLinkMessage", "This document wants to open:\n\n{0}\n\nOnly continue if you trust this address.", "Dieses Dokument möchte Folgendes öffnen:\n\n{0}\n\nFahren Sie nur fort, wenn Sie dieser Adresse vertrauen."),
        ("OpenLink", "Open link", "Link öffnen"),
        ("BlockedLinkTitle", "Blocked", "Blockiert"),
        ("BlockedLaunch", "This document tried to start a program or open a file:\n\n{0}\n\nThis is blocked for your safety.", "Dieses Dokument wollte ein Programm starten oder eine Datei öffnen:\n\n{0}\n\nDies wurde zu Ihrer Sicherheit blockiert."),
        ("BlockedScheme", "Links of this type cannot be opened:\n\n{0}", "Links dieses Typs können nicht geöffnet werden:\n\n{0}"),

        // Dialogs
        ("Cancel", "Cancel", "Abbrechen"),
        ("OK", "OK", "OK"),
        ("Open", "Open", "Öffnen"),
        ("PasswordTitle", "Password required", "Kennwort erforderlich"),
        ("PasswordMessage", "\"{0}\" is protected. Enter the password to open it.", "„{0}“ ist geschützt. Geben Sie das Kennwort ein, um die Datei zu öffnen."),
        ("PasswordWrong", "That password is not correct. Try again.", "Das Kennwort ist falsch. Bitte erneut versuchen."),
        ("CannotOpenTitle", "Can't open this file", "Datei kann nicht geöffnet werden"),
        ("GoToPageTitle", "Go to page", "Gehe zu Seite"),
        ("GoToPageMessage", "Page number (1–{0})", "Seitenzahl (1–{0})"),
        ("PdfFiles", "PDF documents", "PDF-Dokumente"),
        ("Printing", "Printing… {0} of {1}", "Drucken… {0} von {1}"),
        ("PrintFailed", "Printing failed", "Drucken fehlgeschlagen"),
        ("ReadingAloud", "Reading aloud", "Wird vorgelesen"),
        ("Stop", "Stop", "Stopp"),
        ("SpeechUnavailable", "Read aloud is not available. Install a voice in Windows Settings → Time & language → Speech.", "Vorlesen ist nicht verfügbar. Installieren Sie eine Stimme unter Windows-Einstellungen → Zeit und Sprache → Sprache."),
        ("NoTextOnPage", "There is no text to read on this page. Scanned pages need OCR first.", "Auf dieser Seite gibt es keinen Text zum Vorlesen. Gescannte Seiten benötigen zuerst OCR."),

        // Page colors
        ("PageColors", "Page colors", "Seitenfarben"),
        ("ColorNormal", "Normal", "Normal"),
        ("ColorDark", "Dark", "Dunkel"),
        ("ColorSepia", "Sepia", "Sepia"),
        ("ColorCustom", "Custom", "Benutzerdefiniert"),
        ("KeepImagesNatural", "Keep images natural", "Bilder unverändert lassen"),
        ("OnlyThisDocument", "Only for this document", "Nur für dieses Dokument"),
        ("PageColorsNote", "Display only. The PDF, printing and exports keep their original colors.", "Nur Anzeige. Die PDF, Ausdrucke und Exporte behalten ihre Originalfarben."),

        // Commands / tools
        ("CmdOpen", "Open file", "Datei öffnen"),
        ("CmdClose", "Close document", "Dokument schließen"),
        ("CmdNextTab", "Next document", "Nächstes Dokument"),
        ("CmdPrevTab", "Previous document", "Vorheriges Dokument"),
        ("CmdPrint", "Print", "Drucken"),
        ("CmdFind", "Search in document", "Im Dokument suchen"),
        ("CmdFindNext", "Next search result", "Nächster Suchtreffer"),
        ("CmdFindPrev", "Previous search result", "Vorheriger Suchtreffer"),
        ("CmdGoToPage", "Go to page", "Gehe zu Seite"),
        ("CmdFirstPage", "First page", "Erste Seite"),
        ("CmdLastPage", "Last page", "Letzte Seite"),
        ("CmdNextPage", "Next page", "Nächste Seite"),
        ("CmdPrevPage", "Previous page", "Vorherige Seite"),
        ("CmdZoomIn", "Zoom in", "Vergrößern"),
        ("CmdZoomOut", "Zoom out", "Verkleinern"),
        ("CmdFitWidth", "Fit width", "Seitenbreite"),
        ("CmdFitPage", "Fit page", "Ganze Seite"),
        ("CmdActualSize", "Actual size", "Originalgröße"),
        ("CmdThumbnails", "Show thumbnails", "Miniaturansichten anzeigen"),
        ("CmdOutline", "Show bookmarks", "Lesezeichen anzeigen"),
        ("CmdToggleSidebar", "Toggle sidebar", "Seitenleiste ein/aus"),
        ("CmdReadingMode", "Reading mode", "Lesemodus"),
        ("CmdPageColorCycle", "Cycle page colors", "Seitenfarben wechseln"),
        ("CmdPageNormal", "Page colors: Normal", "Seitenfarben: Normal"),
        ("CmdPageDark", "Page colors: Dark", "Seitenfarben: Dunkel"),
        ("CmdPageSepia", "Page colors: Sepia", "Seitenfarben: Sepia"),
        ("CmdPageCustom", "Page colors: Custom", "Seitenfarben: Benutzerdefiniert"),
        ("CmdReadAloud", "Read aloud from this page", "Ab dieser Seite vorlesen"),
        ("CmdStopReading", "Stop reading aloud", "Vorlesen beenden"),
        ("CmdCopy", "Copy selected text", "Ausgewählten Text kopieren"),
        ("CmdSelectAll", "Select all text on page", "Gesamten Text der Seite auswählen"),
        ("CmdPalette", "Command palette", "Befehlspalette"),
        ("CmdTools", "All tools", "Alle Werkzeuge"),
        ("CmdSettings", "Settings", "Einstellungen"),
        ("CmdUpdates", "Check for updates", "Nach Updates suchen"),
        ("PaletteHint", "Type a command…", "Befehl eingeben…"),
        ("NoCommands", "No matching commands", "Keine passenden Befehle"),
        ("GroupView", "View", "Ansicht"),
        ("GroupRead", "Read & navigate", "Lesen & navigieren"),
        ("GroupDocument", "Document", "Dokument"),
        ("GroupApp", "App", "App"),
        ("ComingSoonNote", "Annotate, organize, sign and more arrive in upcoming updates.", "Kommentieren, Organisieren, Signieren und mehr folgen mit den nächsten Updates."),

        // Settings
        ("Settings", "Settings", "Einstellungen"),
        ("General", "General", "Allgemein"),
        ("Appearance", "Appearance", "Darstellung"),
        ("Updates", "Updates", "Updates"),
        ("Close", "Close", "Schließen"),
        ("Language", "Language", "Sprache"),
        ("LanguageSystem", "Windows default", "Windows-Standard"),
        ("RestartToApply", "Restart PDF.Reader to apply.", "Starten Sie PDF.Reader neu, um dies anzuwenden."),
        ("RestoreSession", "Reopen documents from last time", "Dokumente vom letzten Mal wieder öffnen"),
        ("DefaultZoom", "Default zoom", "Standard-Zoom"),
        ("AppTheme", "App theme", "App-Design"),
        ("ThemeSystem", "Use Windows setting", "Windows-Einstellung verwenden"),
        ("ThemeLight", "Light", "Hell"),
        ("ThemeDark", "Dark", "Dunkel"),
        ("CustomColors", "Custom colors", "Benutzerdefinierte Farben"),
        ("TextColor", "Text", "Text"),
        ("BackgroundColor", "Background", "Hintergrund"),
        ("CurrentVersion", "Current version", "Aktuelle Version"),
        ("CheckForUpdates", "Check for updates", "Nach Updates suchen"),
        ("CheckAutomatically", "Check for updates when the app starts", "Beim Start nach Updates suchen"),
        ("UpdateChannel", "Update channel", "Update-Kanal"),
        ("ChannelStable", "Stable (recommended)", "Stabil (empfohlen)"),
        ("ChannelBeta", "Beta: try new features early", "Beta: neue Funktionen früher testen"),
        ("UpToDate", "You're up to date.", "Sie sind auf dem neuesten Stand."),
        ("CheckingUpdates", "Checking for updates…", "Suche nach Updates…"),
        ("UpdateAvailable", "Version {0} is available.", "Version {0} ist verfügbar."),
        ("Downloading", "Downloading… {0}%", "Wird heruntergeladen… {0} %"),
        ("ReadyToInstall", "Version {0} is ready to install.", "Version {0} ist bereit zur Installation."),
        ("RestartToInstall", "Restart to install", "Neu starten und installieren"),
        ("UpdateFailed", "Couldn't check for updates: {0}", "Suche nach Updates fehlgeschlagen: {0}"),
        ("UpdatesNotInstalled", "Updates work in the installed or portable app, not in a development build.", "Updates funktionieren in der installierten oder portablen App, nicht in einem Entwicklungs-Build."),
        ("WhatsNew", "What's new", "Neuigkeiten"),
        ("Privacy", "PDF.Reader never sends data anywhere. The only network request is the update check against GitHub Releases, over HTTPS.", "PDF.Reader sendet niemals Daten. Die einzige Netzwerkanfrage ist die Update-Prüfung bei GitHub Releases über HTTPS."),
    ];
}

/// <summary>XAML: <c>Text="{l:Tr OpenPdf}"</c></summary>
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
