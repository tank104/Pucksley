using Pucksley.Plugins.Windows.Common;
using Pucksley.Plugins.Windows.Pc;
using Pucksley.Sdk;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Tests;

/// <summary>The words pc_screenshot takes; the sizing itself is tested with the shared code (Pucksley.Plugins.Pc.Tests).</summary>
public class ScreenAreaTests
{
    [Theory]
    [InlineData(null, nameof(ScreenArea.ActiveWindow))]
    [InlineData("screen", nameof(ScreenArea.ActiveScreen))]
    [InlineData("all", nameof(ScreenArea.All))]
    [InlineData("primary", nameof(ScreenArea.Primary))]
    [InlineData("active_window", nameof(ScreenArea.ActiveWindow))]
    [InlineData("Active Window", nameof(ScreenArea.ActiveWindow))]
    public void Screen_argument_maps_to_an_area(string? screen, string expected) =>
        Assert.Equal(Enum.Parse<ScreenArea>(expected), PcControlPlugin.ParseArea(screen));

    [Fact]
    public void Unknown_screen_argument_is_explained() =>
        Assert.Contains("all, primary or active_window", Assert.Throws<PluginException>(() => PcControlPlugin.ParseArea("left one")).Message);
}

public class TextTyperTests
{
    [Fact]
    public void Characters_become_unicode_key_down_and_up()
    {
        var strokes = TextTyper.KeyStrokes("Hé");
        Assert.Equal(2, strokes.Count);
        foreach (var (stroke, ch) in strokes.Zip("Hé"))
        {
            Assert.Equal(2, stroke.Length);
            Assert.Equal(INPUT_KEYBOARD, stroke[0].type);
            Assert.Equal(ch, (char)stroke[0].u.ki.wScan);
            Assert.Equal(0, stroke[0].u.ki.wVk);
            Assert.Equal(KEYEVENTF_UNICODE, stroke[0].u.ki.dwFlags);
            Assert.Equal(KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, stroke[1].u.ki.dwFlags);
        }
    }

    [Fact]
    public void Line_breaks_press_enter_once_and_tabs_press_tab()
    {
        var strokes = TextTyper.KeyStrokes("a\r\nb\nc\td\r");
        var keys = strokes.Select(s => s[0].u.ki.wVk == 0 ? ((char)s[0].u.ki.wScan).ToString() : $"<{s[0].u.ki.wVk:X2}>").ToArray();
        Assert.Equal(new[] { "a", "<0D>", "b", "<0D>", "c", "<09>", "d", "<0D>" }, keys);
    }

    [Fact]
    public void A_surrogate_pair_stays_in_one_stroke()
    {
        var stroke = Assert.Single(TextTyper.KeyStrokes("😀"));
        Assert.Equal(4, stroke.Length);
        Assert.True(char.IsHighSurrogate((char)stroke[0].u.ki.wScan));
        Assert.True(char.IsLowSurrogate((char)stroke[2].u.ki.wScan));
    }
}

public class OpenTargetTests
{
    static readonly KnownFolder[] Folders =
    [
        new("Desktop", @"C:\Users\me\Desktop"),
        new("Documents", @"C:\Users\me\Documents"),
        new("Downloads", @"C:\Users\me\Downloads"),
        new("Pictures", @"C:\Users\me\Pictures"),
        new("Recycle Bin", "shell:RecycleBinFolder"),
        new("This PC", "shell:MyComputerFolder"),
    ];

    static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Users\me\Documents\report.docx", @"C:\tools\evil.exe", @"C:\x\y.pdf",
    };

    static readonly HashSet<string> Directories = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Users\me\Downloads", @"D:\", @"C:\Users\me\My Projects\",
    };

    static OpenPlan Resolve(string target) => OpenTarget.Resolve(target, Files.Contains, Directories.Contains, Folders);

    [Theory]
    [InlineData("https://example.com/page?q=1", "https://example.com/page?q=1")]
    [InlineData("youtube.com", "https://youtube.com")]
    [InlineData("www.bbc.co.uk/news", "https://www.bbc.co.uk/news")]
    [InlineData("ms-settings:display", "ms-settings:display")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    public void Links_open_in_their_app(string target, string expected)
    {
        var plan = Resolve(target);
        Assert.Equal(expected, plan.StartInfo.FileName);
        Assert.True(plan.StartInfo.UseShellExecute);
    }

    [Theory]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("search-ms:query=secret")]
    [InlineData("javascript:alert(1)")]
    public void Risky_link_types_are_refused(string target) =>
        Assert.Contains("won't open", Assert.Throws<PluginException>(() => Resolve(target)).Message);

    [Theory]
    [InlineData("Downloads", @"C:\Users\me\Downloads")]
    [InlineData("my downloads folder", @"C:\Users\me\Downloads")]
    [InlineData("documents", @"C:\Users\me\Documents")]
    [InlineData("the recycle bin", "shell:RecycleBinFolder")]
    [InlineData("This PC", "shell:MyComputerFolder")]
    [InlineData(@"C:\Users\me\Downloads", @"C:\Users\me\Downloads")]
    [InlineData(@"C:\Users\me\My Projects\", @"""C:\Users\me\My Projects""")] // spaces: quoted, and no \" at the end
    [InlineData("D drive", @"D:\")]
    [InlineData("D:", @"D:\")]
    public void Folders_open_in_explorer(string target, string expectedArguments)
    {
        var plan = Resolve(target);
        Assert.Equal("explorer.exe", plan.StartInfo.FileName);
        Assert.Equal(expectedArguments, plan.StartInfo.Arguments);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents\report.docx", @"C:\Users\me\Documents\report.docx")]
    [InlineData(@"""C:\Users\me\Documents\report.docx""", @"C:\Users\me\Documents\report.docx")]
    [InlineData("file:///C:/x/y.pdf", @"C:\x\y.pdf")]
    public void Documents_open_with_their_app(string target, string expected) =>
        Assert.Equal(expected, Resolve(target).StartInfo.FileName);

    [Fact]
    public void Programs_are_not_run_through_pc_open() =>
        Assert.Contains("program", Assert.Throws<PluginException>(() => Resolve(@"C:\tools\evil.exe")).Message);

    [Fact]
    public void A_file_name_is_not_mistaken_for_a_website() =>
        Assert.Throws<PluginException>(() => Resolve("notes.txt"));

    [Fact]
    public void Missing_path_says_so() =>
        Assert.Contains("no file or folder", Assert.Throws<PluginException>(() => Resolve(@"C:\nope\missing.txt")).Message);

    [Fact]
    public void Missing_drive_says_so() =>
        Assert.Contains("no Q drive", Assert.Throws<PluginException>(() => Resolve("Q drive")).Message);

    [Fact]
    public void Unknown_target_lists_the_folders_it_knows()
    {
        var e = Assert.Throws<PluginException>(() => Resolve("the garage"));
        Assert.Contains("Downloads", e.Message);
        Assert.Contains("Recycle Bin", e.Message);
    }
}

public class HelperTests
{
    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("MSEdge", "Edge")]
    [InlineData("Chrome", "Chrome")]
    [InlineData("308046B0AF4A39CB", "Firefox")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Media Player")]
    [InlineData(@"C:\Program Files\Foo\foobar2000.exe", "Foobar2000")]
    public void Media_app_ids_become_names(string aumid, string expected) => Assert.Equal(expected, AppNames.GuessFromAumid(aumid));

    [Theory]
    [InlineData(3 * 1440 + 4 * 60, "3 days 4 hours")]
    [InlineData(61, "1 hour 1 minute")]
    [InlineData(5, "5 minutes")]
    [InlineData(0, "0 minutes")]
    public void Durations_read_naturally(int minutes, string expected) =>
        Assert.Equal(expected, SystemInfo.Duration(TimeSpan.FromMinutes(minutes)));
}
