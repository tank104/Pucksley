using Pucksley.Plugins.Windows.Pc;

namespace Pucksley.Plugins.Windows.Tests;

public class AppIndexTests
{
    const string Programs = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\";

    static readonly AppEntry[] Raw =
    [
        new("Outlook", Programs + "Outlook.lnk", AppKind.Shortcut),
        new("Outlook (classic)", Programs + "Outlook (classic).lnk", AppKind.Shortcut),
        new("Google Chrome", Programs + "Google Chrome.lnk", AppKind.Shortcut),
        new("Word", Programs + "Word.lnk", AppKind.Shortcut),
        new("WordPad", Programs + @"Accessories\WordPad.lnk", AppKind.Shortcut),
        new("Spotify", Programs + "Spotify.lnk", AppKind.Shortcut),
        new("Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", AppKind.AppsFolder), // same app twice
        new("Uninstall Spotify", Programs + @"Spotify\Uninstall Spotify.lnk", AppKind.Shortcut),
        new("ReadMe", Programs + @"SomeTool\ReadMe.lnk", AppKind.Shortcut),
        new("Python 3.12 Manuals (64-bit)", @"{6D809377-6AF0-444B-8957-A3773F02200E}\Python312\Doc\python3121.chm", AppKind.AppsFolder),
        new("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", AppKind.AppsFolder),
        new("Settings", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", AppKind.AppsFolder),
        new("Microsoft Teams", "MSTeams_8wekyb3d8bbwe!MSTeams", AppKind.AppsFolder),
        new("Microsoft Teams (work or school)", Programs + "Microsoft Teams (work or school).lnk", AppKind.Shortcut),
        new("Visual Studio Code", Programs + @"Visual Studio Code\Visual Studio Code.lnk", AppKind.Shortcut),
        new("File Explorer", "Microsoft.Windows.Explorer", AppKind.AppsFolder),
        new("Microsoft Edge", Programs + "Microsoft Edge.lnk", AppKind.Shortcut),
    ];

    static readonly IReadOnlyList<AppEntry> Apps = AppIndex.Clean(Raw);

    [Fact]
    public void Clean_drops_uninstallers_readmes_and_documents()
    {
        Assert.DoesNotContain(Apps, a => a.Name.Contains("Uninstall"));
        Assert.DoesNotContain(Apps, a => a.Name == "ReadMe");
        Assert.DoesNotContain(Apps, a => a.Name.StartsWith("Python"));
    }

    [Fact]
    public void Clean_keeps_one_entry_per_name_preferring_the_shortcut()
    {
        var spotify = Assert.Single(Apps, a => a.Name == "Spotify");
        Assert.Equal(AppKind.Shortcut, spotify.Kind);
    }

    [Theory]
    [InlineData("Outlook", "Outlook")]
    [InlineData("outlook classic", "Outlook (classic)")]
    [InlineData("chrome", "Google Chrome")]
    [InlineData("Word", "Word")]
    [InlineData("the calculator app", "Calculator")]
    [InlineData("calculator", "Calculator")]
    [InlineData("settings", "Settings")]
    [InlineData("Teams", "Microsoft Teams")] // shorter name wins the tie
    [InlineData("VS Code", "Visual Studio Code")] // alias
    [InlineData("explorer", "File Explorer")] // alias
    [InlineData("edge", "Microsoft Edge")]
    [InlineData("spotify", "Spotify")]
    public void Match_finds_what_people_say(string spoken, string expected) =>
        Assert.Equal(expected, AppIndex.Match(spoken, Apps)?.Name);

    [Theory]
    [InlineData("Photoshop")]
    [InlineData("the garage door")]
    public void Match_returns_null_for_apps_that_are_not_installed(string spoken) => Assert.Null(AppIndex.Match(spoken, Apps));

    [Fact]
    public void Not_found_message_suggests_the_closest_names()
    {
        var message = AppIndex.NotFoundMessage("Outluk", Apps);
        Assert.Contains("Outluk", message);
        Assert.Contains("Outlook", message);
    }

    [Fact]
    public void Store_apps_launch_through_explorer_and_the_apps_folder()
    {
        var info = AppIndex.LaunchInfo(Apps.Single(a => a.Name == "Calculator"));
        Assert.Equal("explorer.exe", info.FileName);
        Assert.Equal(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", info.Arguments);
        Assert.True(info.UseShellExecute);
    }

    [Fact]
    public void Shortcuts_launch_through_the_shell()
    {
        var info = AppIndex.LaunchInfo(Apps.Single(a => a.Name == "Spotify"));
        Assert.Equal(Programs + "Spotify.lnk", info.FileName);
        Assert.True(info.UseShellExecute);
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

        var first = await index.GetAsync(default);
        await index.GetAsync(default);
        Assert.Equal(1, loads);

        now += TimeSpan.FromMinutes(11);
        var stale = await index.GetAsync(default); // answers at once from the old list…
        Assert.Same(first, stale);
        await Wait.UntilAsync(() => Volatile.Read(ref loads) == 2); // …while it reloads
    }
}
