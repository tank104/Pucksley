using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Pucksley.Core.Plugins;
using Pucksley.Core.Pucks;
using Pucksley.Core.Settings;
using Pucksley.Core.Voice;
using Pucksley.Sdk;

namespace Pucksley.Core.Hosting;

/// <summary>Short-lived files served to LAN devices (announcement audio for Sonos and the like).</summary>
public sealed class ClipStore
{
    readonly ConcurrentDictionary<string, (byte[] Data, string Type, DateTimeOffset Expires)> _clips = new();

    public string Add(byte[] data, string contentType, TimeSpan lifetime)
    {
        foreach (var (k, v) in _clips) if (v.Expires < DateTimeOffset.UtcNow) _clips.TryRemove(k, out _);
        var id = Guid.NewGuid().ToString("n");
        _clips[id] = (data, contentType, DateTimeOffset.UtcNow + lifetime);
        return id;
    }

    public (byte[] Data, string Type)? Get(string id) =>
        _clips.TryGetValue(id, out var c) && c.Expires > DateTimeOffset.UtcNow ? (c.Data, c.Type) : null;
}

/// <summary>What the plugins reach through their context: pucks, speech, clips, facts about the hub.</summary>
public sealed class HubRuntime : IHubRuntime, IHubInfo
{
    readonly SettingsStore _settings;
    readonly Lazy<PuckManager> _pucks;
    readonly Lazy<SpeechService> _speech;
    readonly ClipStore _clips;

    public HubRuntime(SettingsStore settings, Lazy<PuckManager> pucks, Lazy<SpeechService> speech, ClipStore clips, ILogger<HubRuntime> log)
    {
        _settings = settings;
        _pucks = pucks;
        _speech = speech;
        _clips = clips;
        Http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        })
        { Timeout = TimeSpan.FromSeconds(30) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Pucksley/0.1");
    }

    public HttpClient Http { get; }
    public IHubInfo HubInfo => this;

    public TimeZoneInfo TimeZone => _pucks.Value.TimeZone;

    public Task NotifyAsync(string text, NotifyKind kind, bool chime, string? puckId) => _pucks.Value.NotifyAsync(text, kind, chime, puckId);

    public Task SayAsync(string text, string? puckId, CancellationToken cancel) => _pucks.Value.SayAsync(text, puckId, cancel);

    public Task<bool> ConfirmAsync(string question, ToolCaller caller, TimeSpan timeout, CancellationToken cancel) =>
        _pucks.Value.ConfirmAsync(question, caller, timeout, cancel);

    public void ShowTimer(string timerId, DateTimeOffset? endsAt, string? label, string? puckId) => _pucks.Value.ShowTimer(timerId, endsAt, label, puckId);

    public Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancel) => _speech.Value.SynthesizeWavAsync(text, cancel);

    public Uri PublishClip(byte[] data, string contentType, TimeSpan lifetime)
    {
        var id = _clips.Add(data, contentType, lifetime);
        var ext = contentType switch { "audio/wav" or "audio/x-wav" => ".wav", "audio/mpeg" => ".mp3", _ => "" };
        return new Uri(LanUrl, $"clips/{id}{ext}");
    }

    // ---- IHubInfo

    public string Name => _settings.Current.HubName;

    public Uri LanUrl => new($"http://{LanAddress()}:{Port}/");

    public Uri LoopbackUrl => new($"http://127.0.0.1:{Port}/");

    int Port => HubHost.ActualPort > 0 ? HubHost.ActualPort : _settings.Current.Port;

    public double? Latitude => _settings.Current.Household.Latitude;
    public double? Longitude => _settings.Current.Household.Longitude;
    public string? Location => string.IsNullOrWhiteSpace(_settings.Current.Household.Location) ? null : _settings.Current.Household.Location.Trim();
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>The LAN address other devices can reach: the interface with a default gateway, preferring private ranges.</summary>
    public static string LanAddress()
    {
        var env = Environment.GetEnvironmentVariable("PUCKSLEY_LAN_ADDRESS");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => (Props: n.GetIPProperties(), Nic: n))
                .Where(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(x => x.Props.UnicastAddresses.Select(u => u.Address))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .ToList();
            var pick = candidates.FirstOrDefault(IsPrivate) ?? candidates.FirstOrDefault();
            if (pick is not null) return pick.ToString();
        }
        catch (NetworkInformationException) { }
        return "127.0.0.1";
    }

    static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168);
    }
}
