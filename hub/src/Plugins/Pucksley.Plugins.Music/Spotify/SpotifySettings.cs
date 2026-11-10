using Pucksley.Sdk;

namespace Pucksley.Plugins.Spotify;

/// <summary>Spotify settings. Sign-in tokens are managed by the plugin (Connect / Disconnect buttons).</summary>
public sealed class SpotifySettings
{
    [Setting("Client ID", Order = 1, Placeholder = "From your app at developer.spotify.com",
        Help = "Create an app at developer.spotify.com/dashboard, tick Web API, add the redirect URI shown on this card, then paste the app's Client ID here and click Connect.")]
    public string ClientId { get; set; } = "";

    [Setting("Default device", Order = 2, Placeholder = "Kitchen",
        Help = "Spotify Connect device or Sonos room to play on when none is named and the puck's room has none.")]
    public string DefaultDevice { get; set; } = "";

    /// <summary>OAuth refresh token (not shown on the form; saved by the plugin).</summary>
    [Secret]
    public string RefreshToken { get; set; } = "";

    /// <summary>The connected account's display name.</summary>
    public string AccountName { get; set; } = "";
}
