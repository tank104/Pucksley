using Pucksley.Plugins.Pc.Desktop;
using Pucksley.Sdk;
using Pucksley.TestSupport;

namespace Pucksley.Plugins.Pc.Tests;

public class AppIndexTests
{
    static readonly AppEntry[] Raw =
    [
        new("Spotify", "spotify.lnk", AppKind.Shortcut),
        new("Spotify", "SpotifyAB.SpotifyMusic!Spotify", AppKind.AppsFolder), // the same app, listed twice
        new("Uninstall Spotify", "uninstall.lnk", AppKind.Shortcut),
        new("Calculator", "calc!App", AppKind.AppsFolder),
        new("Calculator", "calc.lnk", AppKind.Shortcut), // the better kind arrives second
        new("Files", "/usr/share/applications/org.gnome.Nautilus.desktop", AppKind.DesktopFile),
        new("Files", "/var/lib/flatpak/exports/share/applications/files.desktop", AppKind.DesktopFile), // same kind: the first stays
        new("Chromium", "/usr/share/applications/chromium.desktop", AppKind.DesktopFile) { OtherNames = ["Web Browser", "chromium"] },
        new("Ghostty", "/usr/share/applications/com.mitchellh.ghostty.desktop", AppKind.DesktopFile) { OtherNames = ["Terminal", "ghostty"] },
        new("Microsoft Teams", "teams!App", AppKind.AppsFolder),
        new("Microsoft Teams (work or school)", "teams-work.lnk", AppKind.Shortcut),
        new("Python Manuals", "python.chm", AppKind.AppsFolder),
        new("", "nameless.lnk", AppKind.Shortcut),
    ];

    static readonly IReadOnlyList<AppEntry> Apps = AppIndex.Clean(Raw);

    [Fact]
    public void Clean_drops_clutter_and_keeps_one_entry_per_name()
    {
        Assert.DoesNotContain(Apps, a => a.Name.Contains("Uninstall") || a.Name.Contains("Manuals") || a.Name.Length == 0);
        Assert.Equal(Apps.Count, Apps.Select(a => a.Name).Distinct().Count());
    }

    [Fact]
    public void Among_entries_of_one_name_the_earlier_kind_wins_and_then_the_first_listed()
    {
        Assert.Equal(AppKind.Shortcut, Apps.Single(a => a.Name == "Spotify").Kind);
        Assert.Equal("calc.lnk", Apps.Single(a => a.Name == "Calculator").Target);
        Assert.Equal("/usr/share/applications/org.gnome.Nautilus.desktop", Apps.Single(a => a.Name == "Files").Target);
    }

    [Fact]
    public void The_list_is_sorted_shortest_name_first() =>
        Assert.Equal(Apps.OrderBy(a => a.Name.Length).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => a.Name), Apps.Select(a => a.Name));

    [Theory]
    [InlineData("Spotify", "Spotify")]
    [InlineData("the calculator app", "Calculator")]
    [InlineData("Teams", "Microsoft Teams")] // the shorter name wins the tie
    [InlineData("web browser", "Chromium")]  // by what it is
    [InlineData("terminal", "Ghostty")]
    [InlineData("ghostty", "Ghostty")]
    public void Match_finds_what_people_say_by_name_or_other_names(string spoken, string expected) =>
        Assert.Equal(expected, AppIndex.Match(spoken, Apps)?.Name);

    [Fact]
    public void A_name_counts_for_more_than_an_other_name()
    {
        var apps = AppIndex.Clean([
            new AppEntry("Terminal", "a.desktop", AppKind.DesktopFile),
            new AppEntry("Ghostty", "b.desktop", AppKind.DesktopFile) { OtherNames = ["Terminal"] },
        ]);
        Assert.Equal("Terminal", AppIndex.Match("terminal", apps)?.Name);
        Assert.True(AppIndex.Score("terminal", apps.Single(a => a.Name == "Ghostty")) < 1);
    }

    [Fact]
    public void Aliases_are_looked_for_in_order_instead_of_what_was_said()
    {
        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["explorer"] = ["File Manager", "Files"],
            ["music"] = ["Rhythmbox"],
        };
        Assert.Equal("Files", AppIndex.Match("explorer", Apps, aliases)?.Name);
        Assert.Equal("Files", AppIndex.Match("explorer app", Apps, aliases)?.Name); // "app" is dropped before the alias is looked up
        Assert.Null(AppIndex.Match("music", Apps, aliases)); // what the alias names isn't installed
        Assert.Null(AppIndex.Match("explorer", Apps)); // without the alias, nothing is called that
    }

    [Theory]
    [InlineData("Photoshop")]
    [InlineData("the garage door")]
    [InlineData("   ")]
    public void Match_returns_null_for_apps_that_are_not_installed(string spoken) => Assert.Null(AppIndex.Match(spoken, Apps));

    [Fact]
    public void Not_found_message_suggests_the_closest_names()
    {
        var message = AppIndex.NotFoundMessage("Spotfy", Apps);
        Assert.Contains("Spotfy", message);
        Assert.Contains("Closest: Spotify", message);
        Assert.Equal("I can't find an app called 'Zzz' on the PC.", AppIndex.NotFoundMessage("Zzz", Apps));
    }

    [Fact]
    public async Task The_index_is_cached_and_refreshed_in_the_background_after_ten_minutes()
    {
        int loads = 0;
        var now = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        var index = new AppIndex(_ =>
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult<IReadOnlyList<AppEntry>>(Raw);
        }, () => now);

        Assert.Null(index.Known);
        var first = await index.GetAsync(default);
        await index.GetAsync(default);
        Assert.Equal(1, loads);
        Assert.Same(first, index.Known);

        now += TimeSpan.FromMinutes(11);
        var stale = await index.GetAsync(default); // answers at once from the old list…
        Assert.Same(first, stale);
        await Wait.UntilAsync(() => Volatile.Read(ref loads) == 2); // …while it reloads
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_old_list()
    {
        int loads = 0;
        var now = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        var index = new AppIndex(_ => Interlocked.Increment(ref loads) == 1
            ? Task.FromResult<IReadOnlyList<AppEntry>>(Raw)
            : Task.FromException<IReadOnlyList<AppEntry>>(new IOException("disk gone")), () => now);
        var first = await index.GetAsync(default);
        now += TimeSpan.FromMinutes(11);
        await index.GetAsync(default);
        await Wait.UntilAsync(() => Volatile.Read(ref loads) == 2);
        Assert.Same(first, await index.GetAsync(default));
    }
}

public class WindowMatcherTests
{
    static readonly AppWindow[] Windows =
    [
        new(1, "Inbox - me@example.com - Outlook", 10, "olk", "Microsoft Outlook"),
        new(2, "YouTube - Google Chrome", 11, "chrome", "Google Chrome"),
        new(3, "Budget 2026.xlsx - Excel", 12, "EXCEL", "Microsoft Excel"),
        new(4, "Calculator", 13, "CalculatorApp", "Calculator"),
        new(unchecked((nint)0x56398c5d62e0), "~/code — nvim", 14, "ghostty", "Ghostty"), // a Linux compositor's window address fits too
    ];

    [Theory]
    [InlineData("Outlook", 1)]
    [InlineData("chrome", 2)]
    [InlineData("budget", 3)] // from the title
    [InlineData("excel", 3)]
    [InlineData("calculator", 4)]
    [InlineData("the ghostty window", 0x56398c5d62e0)]
    public void Finds_windows_by_app_or_title(string spoken, long handle) =>
        Assert.Equal(handle, (long)WindowMatcher.Match(spoken, Windows)!.Handle);

    [Fact]
    public void Ties_go_to_the_window_nearest_the_front()
    {
        AppWindow[] two = [new(7, "Notes", 1, "edge", "Microsoft Edge"), new(8, "News", 1, "edge", "Microsoft Edge")];
        Assert.Equal(7, (int)WindowMatcher.Match("edge", two)!.Handle);
    }

    [Fact]
    public void Apps_that_are_not_open_are_not_matched() => Assert.Null(WindowMatcher.Match("Spotify", Windows));
}

public class OpenRulesTests
{
    static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "tel" };
    const string CanOpen = "web pages, mail links, files and folders";

    [Fact]
    public void What_was_asked_for_loses_its_quotes()
    {
        Assert.Equal("youtube.com", OpenRules.Clean("  \"youtube.com\" "));
        Assert.Equal("/home/me/a file.txt", OpenRules.Clean("'/home/me/a file.txt'"));
        Assert.Contains("Open what?", Assert.Throws<PluginException>(() => OpenRules.Clean(" \"\" ")).Message);
    }

    [Theory]
    [InlineData("https://example.com/page?q=1", "https://example.com/page?q=1", "example.com")]
    [InlineData("HTTP://Example.com", "http://example.com/", "example.com")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com", "example.com")]
    [InlineData("tel:+6441234567", "tel:+6441234567", "tel:+6441234567")] // no site in it: the link itself
    public void Allowed_links_are_recognised(string target, string address, string described)
    {
        var link = OpenRules.Link(target, Allowed, CanOpen);
        Assert.NotNull(link);
        Assert.Equal(address, link.AbsoluteUri);
        Assert.Equal(described, OpenRules.Describe(link, target));
    }

    [Theory]
    [InlineData("javascript:alert(1)", "javascript")]
    [InlineData("ms-msdt:/id PCWDiagnostic", "ms-msdt")]
    [InlineData("search-ms:query=secret", "search-ms")]
    [InlineData("vscode://file/etc/passwd", "vscode")]
    public void Other_link_types_are_refused_by_their_scheme_alone(string target, string scheme)
    {
        var e = Assert.Throws<PluginException>(() => OpenRules.Link(target, Allowed, CanOpen));
        Assert.Equal($"I won't open '{scheme}:' links. I can open {CanOpen}.", e.Message);
    }

    [Theory]
    [InlineData("youtube.com")]          // a web address without its scheme: not a link yet
    [InlineData("/home/me/notes.txt")]
    [InlineData(@"C:\Users\me\notes.txt")] // a drive letter isn't a scheme
    [InlineData("file:///home/me/notes.txt")] // a path in disguise: the system's half handles it
    [InlineData("Downloads")]
    public void Paths_and_names_are_not_links(string target) => Assert.Null(OpenRules.Link(target, Allowed, CanOpen));

    [Theory]
    [InlineData("youtube.com", "https://youtube.com")]
    [InlineData("www.bbc.co.uk/news", "https://www.bbc.co.uk/news")]
    [InlineData("localhost.example:8080/x", "https://localhost.example:8080/x")]
    public void A_bare_web_address_gets_https(string target, string expected) => Assert.Equal(expected, OpenRules.WebAddress(target));

    [Theory]
    [InlineData("notes.txt")]    // a file's name, not a site
    [InlineData("report.pdf")]
    [InlineData("pucksley.desktop")]
    [InlineData("two words.com")]
    [InlineData("Downloads")]
    public void File_names_and_plain_words_are_not_web_addresses(string target) => Assert.Null(OpenRules.WebAddress(target));

    [Fact]
    public void Folders_are_found_by_the_names_people_use()
    {
        KnownFolder[] folders = [new("Downloads", "/home/me/Downloads"), new("Documents", "/home/me/Documents"), new("Trash", "/home/me/.local/share/Trash/files")];
        Assert.Equal("/home/me/Downloads", OpenRules.NamedFolder("my downloads folder", folders)?.Path);
        Assert.Equal("Trash", OpenRules.NamedFolder("the trash", folders)?.Name);
        Assert.Null(OpenRules.NamedFolder("the garage", folders));
        var e = OpenRules.NotFound("the garage", folders);
        Assert.Equal("I couldn't find 'the garage'. I can open web addresses, full file or folder paths, and these folders: Downloads, Documents, Trash.", e.Message);
    }
}

public class TypedTextTests
{
    static string Show(IEnumerable<TypedUnit> units) => string.Join("|", units.Select(u => u.Kind switch { TypedKind.Enter => "<Enter>", TypedKind.Tab => "<Tab>", _ => u.Text }));

    [Fact]
    public void Each_character_is_one_unit() => Assert.Equal("H|é|!", Show(TypedText.Units("Hé!")));

    [Fact]
    public void Line_breaks_press_enter_once_and_tabs_press_tab() =>
        Assert.Equal("a|<Enter>|b|<Enter>|c|<Tab>|d|<Enter>", Show(TypedText.Units("a\r\nb\nc\td\r")));

    [Fact]
    public void A_surrogate_pair_stays_in_one_unit()
    {
        var unit = Assert.Single(TypedText.Units("😀"));
        Assert.Equal(TypedKind.Character, unit.Kind);
        Assert.Equal(2, unit.Text.Length);
    }

    [Fact]
    public void A_lone_surrogate_is_still_a_unit() => Assert.Single(TypedText.Units("\uD83D"));

    [Fact]
    public void Runs_join_text_and_count_keys()
    {
        var runs = TypedText.Runs("Dear Sam,\r\n\r\nThanks\t\tAndrew");
        Assert.Equal(
            new[] { (TypedKind.Character, "Dear Sam,", 9), (TypedKind.Enter, "\n", 2), (TypedKind.Character, "Thanks", 6), (TypedKind.Tab, "\t", 2), (TypedKind.Character, "Andrew", 6) },
            runs);
        Assert.Empty(TypedText.Runs(""));
    }
}

public class SystemFactsTests
{
    [Theory]
    [InlineData(80, true, true, null, "Battery 80%, charging")]
    [InlineData(100, false, true, null, "Battery 100%, plugged in")]
    [InlineData(45, false, false, 130, "Battery 45%, about 2 hours 10 minutes left")]
    [InlineData(45, false, false, null, "Battery 45%, on battery")]
    [InlineData(null, false, false, null, "Battery, on battery")]
    public void Battery_reads_naturally(int? percent, bool charging, bool pluggedIn, int? minutesLeft, string expected) =>
        Assert.Equal(expected, SystemFacts.BatteryText(percent, charging, pluggedIn, minutesLeft is int m ? TimeSpan.FromMinutes(m) : null));

    [Fact]
    public void Memory_and_drives_read_naturally()
    {
        Assert.Equal("Memory 7.2 of 31.9 GB used (23%)", SystemFacts.MemoryText(7.2, 31.9, 23));
        Assert.Equal("C: 120 GB free of 476 GB", SystemFacts.DriveText("C:", 120.4, 476.2));
    }

    [Fact]
    public void The_paragraph_lists_what_is_known_in_order()
    {
        var text = new SystemFacts
        {
            Machine = "STUDY-PC",
            OsVersion = "Test OS 1.0",
            CpuPercent = 12,
            Processor = "Fast Chip 9000",
            Memory = SystemFacts.MemoryText(8, 16, 50),
            Drive = null, // couldn't be read: left out
            Uptime = TimeSpan.FromHours(27),
        }.Describe();
        Assert.StartsWith("STUDY-PC: Test OS 1.0. CPU 12% (Fast Chip 9000). Memory 8.0 of 16.0 GB used (50%). Up 1 day 3 hours. ", text);
        Assert.EndsWith(". No battery.", text);
        Assert.True(text.Contains("IP ") || text.Contains("No network address"));
    }
}
