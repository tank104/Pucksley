using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Pucksley.Plugins.Linux.Audio;
using Pucksley.Plugins.Linux.Common;
using Pucksley.Sdk;
using Pucksley.TestSupport;
using Tmds.DBus.Protocol;
using Xunit.Abstractions;

namespace Pucksley.Plugins.Linux.Tests;

/// <summary>
/// The media-player code end to end, over the real session bus, against a player that exists only
/// for the test (it plays nothing). Nothing of the person's is touched; without a session bus the
/// test just says so.
/// </summary>
[Trait("Category", "Live")]
public class LiveMediaPlayerTests(ITestOutputHelper output)
{
    /// <summary>A pretend media player on the bus: it has a track, and takes play, pause and next.</summary>
    sealed class PretendPlayer : IPathMethodHandler, IDisposable
    {
        readonly DBusConnection _connection;
        public string BusName { get; } = $"org.mpris.MediaPlayer2.pucksleytest{Environment.ProcessId}";
        public ConcurrentQueue<string> Received { get; } = new();
        public string Status = "Playing";
        public string Title = "Teardrop";

        PretendPlayer(DBusConnection connection) => _connection = connection;

        public static async Task<PretendPlayer> StartAsync()
        {
            var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync();
            var player = new PretendPlayer(connection);
            connection.AddMethodHandler(player);
            await connection.RequestNameAsync(player.BusName, RequestNameOptions.None);
            return player;
        }

        public string Path => "/org/mpris/MediaPlayer2";
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            string member = request.MemberAsString ?? "";
            if (context.IsPropertiesInterfaceRequest)
            {
                var reader = request.GetBodyReader();
                string @interface = reader.ReadString();
                if (member == "GetAll" && @interface == "org.mpris.MediaPlayer2.Player")
                {
                    var w = context.CreateReplyWriter("a{sv}");
                    try
                    {
                        WritePlayerProperties(ref w);
                        context.Reply(w.CreateMessage());
                    }
                    finally { w.Dispose(); }
                    return default;
                }
                if (member == "Get" && @interface == "org.mpris.MediaPlayer2" && reader.ReadString() == "Identity")
                {
                    var w = context.CreateReplyWriter("v");
                    try
                    {
                        w.WriteVariantString("Pretend Player");
                        context.Reply(w.CreateMessage());
                    }
                    finally { w.Dispose(); }
                    return default;
                }
                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "no such property");
                return default;
            }
            if (request.InterfaceAsString == "org.mpris.MediaPlayer2.Player")
            {
                Received.Enqueue(member);
                switch (member)
                {
                    case "Pause": Status = "Paused"; break;
                    case "Play": Status = "Playing"; break;
                    case "PlayPause": Status = Status == "Playing" ? "Paused" : "Playing"; break;
                    case "Next": Title = "Angel"; break;
                }
                var w = context.CreateReplyWriter(null!);
                try { context.Reply(w.CreateMessage()); }
                finally { w.Dispose(); }
                AnnounceChange();
                return default;
            }
            context.ReplyUnknownMethodError();
            return default;
        }

        void WritePlayerProperties(ref MessageWriter w)
        {
            var properties = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("PlaybackStatus");
            w.WriteVariantString(Status);
            w.WriteDictionaryEntryStart();
            w.WriteString("Metadata");
            w.WriteSignature("a{sv}");
            var metadata = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("xesam:title");
            w.WriteVariantString(Title);
            w.WriteDictionaryEntryStart();
            w.WriteString("xesam:artist");
            w.WriteSignature("as");
            w.WriteArray(new[] { "Massive Attack" });
            w.WriteDictionaryEntryStart();
            w.WriteString("xesam:album");
            w.WriteVariantString("Mezzanine");
            w.WriteDictionaryEnd(metadata);
            w.WriteDictionaryEnd(properties);
        }

        /// <summary>What a real player sends when its track or state changes.</summary>
        public void AnnounceChange()
        {
            var w = _connection.GetMessageWriter();
            try
            {
                w.WriteSignalHeader(null, Path, "org.freedesktop.DBus.Properties", "PropertiesChanged", "sa{sv}as");
                w.WriteString("org.mpris.MediaPlayer2.Player");
                WritePlayerProperties(ref w);
                w.WriteArray(Array.Empty<string>());
                _connection.TrySendMessage(w.CreateMessage());
            }
            finally { w.Dispose(); }
        }

        public void Dispose() => _connection.Dispose();
    }

    [Fact]
    public async Task A_player_on_the_bus_is_read_followed_and_controlled()
    {
        if (!Bus.Session.Exists)
        {
            output.WriteLine("No session bus");
            return;
        }
        PretendPlayer player;
        try
        {
            player = await PretendPlayer.StartAsync();
        }
        catch (Exception e) when (e is DBusExceptionBase or IOException)
        {
            output.WriteLine($"No session bus: {e.Message}");
            return;
        }
        using (player)
        using (var media = new MprisMedia(NullLogger.Instance))
        {
            int changes = 0;
            media.Changed += () => Interlocked.Increment(ref changes);
            await media.StartAsync(default);

            // Another real player may be playing on this PC: only go on if ours is the one in front
            var current = media.Current;
            output.WriteLine($"Now: {current?.App} \"{current?.Title}\" ({(current?.Playing == true ? "playing" : "not playing")})");
            if (current?.App != "Pretend Player")
            {
                output.WriteLine("Another player is in front; leaving it alone");
                return;
            }
            Assert.Equal(new MediaInfo("Teardrop", "Massive Attack", "Mezzanine", "Pretend Player", true), current);

            // Skipping: the command arrives, and the new track comes back through the change signal
            Assert.True(await media.SendAsync(MediaAction.Next, default));
            await Wait.UntilAsync(() => media.Current?.Title == "Angel", 5000);
            Assert.Contains("Next", player.Received);
            Assert.True(changes >= 1);

            Assert.True(await media.SendAsync(MediaAction.Pause, default));
            await Wait.UntilAsync(() => media.Current?.Playing == false, 5000);
            Assert.False(await media.SendAsync(MediaAction.Pause, default)); // nothing is playing now
            Assert.True(await media.SendAsync(MediaAction.Toggle, default));
            await Wait.UntilAsync(() => media.Current?.Playing == true, 5000);
            Assert.True(await media.SendAsync(MediaAction.Play, default));   // already playing: nothing to send
            Assert.Equal(new[] { "Next", "Pause", "PlayPause" }, player.Received);

            // A change the player makes by itself is noticed too
            player.Title = "Risingson";
            player.AnnounceChange();
            await Wait.UntilAsync(() => media.Current?.Title == "Risingson", 5000);
            output.WriteLine($"Followed to \"{media.Current?.Title}\" after {changes} changes");
        }

        // Gone from the bus: nothing is playing (unless something real is)
        using var after = new MprisMedia(NullLogger.Instance);
        await after.StartAsync(default);
        Assert.NotEqual("Pretend Player", after.Current?.App);
    }
}
