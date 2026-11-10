using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Pucksley.Core.Pucks;
using Pucksley.Core.Tests.Support;
using Pucksley.Core.Web;

namespace Pucksley.Core.Tests;

/// <summary>
/// The USB link's line handling. A SerialPuckConnection normally needs a real port; here it's built
/// on a port that is never opened, and its line parser and outbox are driven directly.
/// </summary>
public sealed class SerialConnectionTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    static SerialPuckConnection Make()
    {
        var ctor = typeof(SerialPuckConnection).GetConstructor(Private, [typeof(string), typeof(SerialPort)])!;
        return (SerialPuckConnection)ctor.Invoke(["COM_TEST", new SerialPort("COM_TEST")]);
    }

    static void Line(SerialPuckConnection c, string line) => typeof(SerialPuckConnection).GetMethod("HandleLine", Private)!.Invoke(c, [line]);

    static List<string> Written(SerialPuckConnection c)
    {
        var outbox = (Channel<byte[]>)typeof(SerialPuckConnection).GetField("_outbox", Private)!.GetValue(c)!;
        var lines = new List<string>();
        while (outbox.Reader.TryRead(out var b)) lines.Add(Encoding.UTF8.GetString(b));
        return lines;
    }

    [Fact]
    public void Json_lines_become_messages_and_everything_else_is_ignored()
    {
        var c = Make();
        var messages = new List<JsonObject>();
        c.MessageReceived += (_, m) => messages.Add(m);
        Line(c, "I (1234) boot: ESP-IDF v5.3 2nd stage bootloader");
        Line(c, "{not json");
        Line(c, "[1,2,3]");
        Line(c, """{"t":5}""");                 // a number where the type should be: ignored, not fatal
        Line(c, """{"t":"au","d":7}""");
        Line(c, """{"t":"hello","id":"a1b2c3d4e5f60708","v":1}""");
        Assert.Equal(["5", "hello"], messages.Select(m => m["t"]!.ToJsonString().Trim('"')));   // the hub then ignores the first
        Assert.Equal("usb", c.Kind);
        Assert.True(c.IsTrusted);
    }

    [Fact]
    public void Audio_lines_become_samples()
    {
        var c = Make();
        var audio = new List<short[]>();
        c.AudioReceived += (_, s) => audio.Add(s);
        var samples = Signals.Sine(440, 16000, 320, 5000);
        Line(c, $$"""{"t":"au","d":"{{Convert.ToBase64String(MemoryMarshal.AsBytes(samples.AsSpan()))}}"}""");
        Line(c, """{"t":"au","d":"not base64!!"}""");
        Assert.Equal(samples, Assert.Single(audio));
    }

    [Fact]
    public void Messages_go_out_as_one_clean_line_each()
    {
        var c = Make();
        c.Send(new JsonObject { ["t"] = "reply", ["text"] = "It’s 21°C in Tāmaki", ["done"] = true });
        var line = Assert.Single(Written(c));
        Assert.EndsWith("\n", line);
        Assert.Equal(1, line.Count(ch => ch == '\n'));
        Assert.Equal("""{"t":"reply","text":"It's 21°C in Tamaki","done":true}""" + "\n", line);   // no ' escapes: lines must stay short
    }

    [Fact]
    public void Speech_goes_out_in_short_base64_lines()
    {
        var c = Make();
        var samples = Signals.Sine(300, 16000, 1000, 7000);   // 2000 bytes → 640 + 640 + 640 + 80
        c.SendAudio(samples);
        var lines = Written(c);
        Assert.Equal(4, lines.Count);
        var decoded = new List<short>();
        foreach (var l in lines)
        {
            Assert.True(Encoding.UTF8.GetByteCount(l) <= 1024, $"line of {l.Length}");
            var json = JsonNode.Parse(l)!;
            Assert.Equal("au", json["t"]!.GetValue<string>());
            var bytes = Convert.FromBase64String(json["d"]!.GetValue<string>());
            Assert.True(bytes.Length <= 640);
            decoded.AddRange(MemoryMarshal.Cast<byte, short>(bytes).ToArray());
        }
        Assert.Equal(samples, decoded);
    }

    [Fact]
    public void Closing_an_unopened_link_is_safe_and_reported_once()
    {
        var c = Make();
        int closed = 0;
        c.Closed += (_, _) => closed++;
        c.Close("test");
        c.Close("again");
        Assert.True(c.IsClosed);
        Assert.Equal(1, closed);
        c.Send(new JsonObject { ["t"] = "ping" });   // ignored once closed
        Assert.Empty(Written(c));
    }
}

public sealed class HubEventsTests
{
    [Fact]
    public async Task Status_events_are_throttled_per_kind_and_the_last_change_still_comes_through()
    {
        var events = new HubEvents();
        var got = new ConcurrentQueue<JsonObject>();
        using var cts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            try { await foreach (var e in events.SubscribeAsync(cts.Token)) got.Enqueue(e); }
            catch (OperationCanceledException) { }
        });
        await HubFixture.WaitUntilAsync(() =>
        {
            events.Publish("probe", new JsonObject());
            return got.Any(e => e["type"]!.GetValue<string>() == "probe");
        }, "subscribed");
        int Count(string what) => got.Count(e => e["type"]!.GetValue<string>() == "status" && e["what"]!.GetValue<string>() == what);

        // (the subscriber reads asynchronously, so "at once" means well inside the 250 ms window)
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) events.StatusChanged("plugins");
        events.StatusChanged("pucks");
        await HubFixture.WaitUntilAsync(() => Count("plugins") == 1 && Count("pucks") == 1, "the first of each kind");
        Assert.True(sw.ElapsedMilliseconds < 150, $"first event took {sw.ElapsedMilliseconds} ms");
        await Task.Delay(700);
        Assert.Equal(2, Count("plugins"));   // the other 49 became one, at the end of the window
        Assert.Equal(1, Count("pucks"));     // kinds don't hold each other up

        // After a quiet spell the next one is immediate again
        sw.Restart();
        events.StatusChanged("plugins");
        await HubFixture.WaitUntilAsync(() => Count("plugins") == 3, "the next event");
        Assert.True(sw.ElapsedMilliseconds < 150, $"next event took {sw.ElapsedMilliseconds} ms");
        cts.Cancel();
        await reader;
    }

    [Fact]
    public void Logs_and_conversation_keep_a_bounded_history()
    {
        var events = new HubEvents();
        for (int i = 0; i < 600; i++) events.Log(Microsoft.Extensions.Logging.LogLevel.Information, "Test", $"line {i}");
        for (int i = 0; i < 250; i++) events.Conversation("s", "web", "user", $"q {i}");
        Assert.Equal(500, events.RecentLogs.Count);
        Assert.Equal("line 599", events.RecentLogs[^1]["message"]!.GetValue<string>());
        Assert.Equal(200, events.RecentConversation.Count);
    }
}
