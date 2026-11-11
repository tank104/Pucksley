using System.Text.Json.Nodes;
using Pucksley.Core.Tests.Support;
using static Pucksley.Core.Tests.Support.Claude;

namespace Pucksley.Core.Tests;

/// <summary>The voice loop end to end: a fake puck talks, the fake STT hears, the fake Claude answers, the fake TTS speaks.</summary>
public sealed class HubVoiceTests(HubFixture hub) : HubTestBase(hub)
{
    const int Rate = 16000;

    /// <summary>talk start → listening → 1 s of mic audio → talk stop; returns everything up to spk end.</summary>
    async Task<List<PuckFrame>> AskAsync(PuckClient puck, short[]? audio = null, string via = "tap")
    {
        await puck.TalkAsync("start", via);
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(audio ?? Signals.Hiss(Rate));
        await puck.TalkAsync("stop");
        return await puck.CollectUntilAsync(f => Is(f, "spk", "a", "end"));
    }

    static int Index(List<PuckFrame> frames, Func<PuckFrame, bool> match) => frames.FindIndex(f => match(f));

    [Fact]
    public async Task A_web_search_before_any_answer_is_covered_by_a_spoken_let_me_check()
    {
        Hub.Claude.Then(Reply("end_turn", [ServerToolUse("srvtoolu_v1", "web_search", """{"query":"wellington weather"}"""), WebSearchResult("srvtoolu_v1"), TextBlock("It's sunny in Wellington.")], delayMs: 5));
        await using var puck = await Hub.ConnectApprovedAsync();

        var frames = await AskAsync(puck);
        var replies = frames.Concat(await puck.DrainAsync(TimeSpan.FromMilliseconds(100))).Where(f => f.Type == "reply").Select(f => f.Json!).ToList();

        // Said first, once; not part of the answer on the screen
        Assert.Equal(["Let me check.", "It's sunny in Wellington."], Hub.Speech.Spoken);
        Assert.Equal("It's sunny in Wellington.", S(replies.Single(r => r["done"]!.GetValue<bool>()), "text"));
    }

    [Fact]
    public async Task A_whole_turn_with_a_tool_ends_idle()
    {
        Hub.Claude
            .Then(Reply("tool_use", [TextBlock("One moment."), ToolUse("toolu_k1", "hub_tools_lights", """{"room":"Kitchen","level":30}""")], delayMs: 5))
            .Then(Reply("end_turn", [TextBlock("Done, the kitchen lights are at thirty percent. They look lovely.")], delayMs: 40));
        await using var puck = await Hub.ConnectApprovedAsync();

        var frames = await AskAsync(puck);

        // The order the puck sees things in
        int thinking = Index(frames, f => f.Json is { } m && IsAi(m, "thinking"));
        int heard = Index(frames, f => f.Type == "heard");
        int actRun = Index(frames, f => Is(f, "act", "s", "run"));
        int actOk = Index(frames, f => Is(f, "act", "s", "ok"));
        int spkStart = Index(frames, f => Is(f, "spk", "a", "start"));
        int firstAudio = Index(frames, f => f.IsAudio);
        int spkEnd = frames.Count - 1;
        Assert.True(thinking == 0, string.Join(" | ", frames.Take(5)));
        Assert.True(thinking < heard && heard < actRun && actRun < actOk && actOk < spkStart && spkStart < firstAudio && firstAudio < spkEnd,
            string.Join(" | ", frames.Where(f => !f.IsAudio)));
        Assert.Contains(frames, f => f.Json is { } m && IsAi(m, "speaking"));

        Assert.Equal(HubFixture.Transcript, S(frames[heard].Json!, "text"));
        Assert.Equal(("light", "Hub tools: Lights"), (S(frames[actRun].Json!, "icon"), S(frames[actRun].Json!, "text")));
        Assert.Equal(("light", "Kitchen 30%"), (S(frames[actOk].Json!, "icon"), S(frames[actOk].Json!, "text")));

        // Speech: whole samples, 20-60 ms frames, about as long as the TTS made (resampled 24 kHz → 16 kHz)
        var audio = frames.Where(f => f.IsAudio).Select(f => f.Audio!).ToList();
        Assert.All(audio, a => Assert.True(a.Length % 2 == 0 && a.Length <= 1920 && a.Length > 0, $"frame of {a.Length} bytes"));
        int samples = audio.Sum(a => a.Length) / 2;
        double expected = Hub.Speech.Spoken.Count * Hub.Speech.SecondsPerSentence * Rate;
        Assert.InRange(samples, expected * 0.95, expected * 1.05);
        var id = frames[spkStart].Json!["id"]!.GetValue<int>();
        Assert.Equal(id, frames[spkEnd].Json!["id"]!.GetValue<int>());

        // Streamed replies grow towards the final one; the final one is complete
        var replies = frames.Concat(await puck.DrainAsync(TimeSpan.FromMilliseconds(100))).Where(f => f.Type == "reply").Select(f => f.Json!).ToList();
        var done = replies.Single(r => r["done"]!.GetValue<bool>());
        const string answer = "One moment. Done, the kitchen lights are at thirty percent. They look lovely.";
        Assert.Equal(answer, S(done, "text"));
        Assert.All(replies.Where(r => !r["done"]!.GetValue<bool>()), r => Assert.StartsWith(S(r, "text")!, answer));
        Assert.Equal(answer, string.Join(" ", Hub.Speech.Spoken));

        // The puck finished playing: back to idle (no question asked, so no follow-up)
        await puck.SpeakerDoneAsync(id);
        await puck.WaitForAsync("ai", m => IsAi(m, "idle"), TimeSpan.FromSeconds(2));

        // Behind the scenes: the STT got the 16 kHz clip, the tool ran for this puck, and Claude got the result
        var clip = Assert.Single(Hub.Speech.Heard);
        Assert.Equal(Rate, clip.SampleRate);
        Assert.Equal(Rate, clip.Samples.Length);   // no speech found by the VAD in hiss: the whole clip is kept
        Assert.Equal([$"lights Kitchen 30 {puck.Id}"], Hub.Tools.Calls);
        var first = Hub.Claude.Requests[0];
        Assert.Contains("Speaking to: Puck-TEST", first.LastUserText());
        Assert.EndsWith("[/Context]\n\n" + HubFixture.Transcript, first.LastUserText());
        var result = Hub.Claude.Requests[1].Messages.Last()!["content"]![0]!;
        Assert.Equal(("tool_result", "toolu_k1"), (S(result.AsObject(), "type"), S(result.AsObject(), "tool_use_id")));
    }

    [Fact]
    public async Task An_answer_that_asks_a_question_listens_again()
    {
        Hub.Claude.Then(Text("Which room did you mean?"));
        await using var puck = await Hub.ConnectApprovedAsync();
        var frames = await AskAsync(puck);
        var id = frames.Last().Json!["id"]!.GetValue<int>();
        await puck.SpeakerDoneAsync(id);
        await puck.WaitForAsync("listen", timeout: TimeSpan.FromSeconds(2));

        // The puck answers a listen with talk start via hub, and the follow-up runs like any turn
        Hub.Claude.Then(Text("Okay, the lounge."));
        Hub.Speech.Transcript = "the lounge";
        var again = await AskAsync(puck, via: "hub");
        Assert.Contains(again, f => Is(f, "heard", "text", "the lounge"));
        // …in the same conversation
        Assert.Equal(3, Hub.Claude.Requests[1].Messages.Count);
    }

    [Fact]
    public async Task With_follow_up_listening_off_a_question_ends_idle()
    {
        (await Hub.PutJsonAsync("api/settings/assistant", new JsonObject { ["followUpListening"] = false })).EnsureSuccessStatusCode();
        Hub.Claude.Then(Text("Anything else?"));
        await using var puck = await Hub.ConnectApprovedAsync();
        var frames = await AskAsync(puck);
        await puck.SpeakerDoneAsync(frames.Last().Json!["id"]!.GetValue<int>());
        await puck.WaitForAsync("ai", m => IsAi(m, "idle"), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Talking_while_the_hub_speaks_interrupts_it()
    {
        Hub.Speech.SecondsPerSentence = 3;
        Hub.Claude.Then(Text("Here is a long story about the kitchen. It goes on and on for a while."));
        await using var puck = await Hub.ConnectApprovedAsync();
        await puck.TalkAsync("start");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");
        await puck.WaitForAsync("spk", m => S(m, "a") == "start");
        await puck.CollectUntilAsync(f => f.IsAudio);

        await puck.TalkAsync("start");
        var cut = await puck.CollectUntilAsync(f => f.Json is { } m && IsAi(m, "listening"));
        Assert.Contains(cut, f => Is(f, "spk", "a", "stop"));
        // The speech really stops: no more audio after the stop, and the old turn doesn't finish
        var after = await puck.DrainAsync(TimeSpan.FromMilliseconds(1200));
        Assert.True(after.Count(f => f.IsAudio) <= 2, $"{after.Count(f => f.IsAudio)} audio frames after spk stop");
        Assert.DoesNotContain(after, f => f.Type == "ai" || Is(f, "spk", "a", "end") || Is(f, "reply"));

        // …and the new question works
        Hub.Speech.SecondsPerSentence = 0.25;
        Hub.Claude.Then(Text("Sure."));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");
        await puck.WaitForAsync("reply", m => m["done"]!.GetValue<bool>() && S(m, "text") == "Sure.");
    }

    [Fact]
    public async Task Talking_during_an_announcement_stops_it()
    {
        // Announcements from plugins (timers, reminders) and the page's "say" share the speaker
        Hub.Speech.SecondsPerSentence = 3;
        await using var puck = await Hub.ConnectApprovedAsync();
        (await Hub.PostJsonAsync($"api/pucks/{puck.Id}/say", new JsonObject { ["text"] = "Your timer is done. The oven is ready for the bread now." })).EnsureSuccessStatusCode();
        await puck.WaitForAsync("spk", m => S(m, "a") == "start");
        await puck.CollectUntilAsync(f => f.IsAudio);

        await puck.TalkAsync("start");
        var cut = await puck.CollectUntilAsync(f => f.Json is { } m && IsAi(m, "listening"));
        Assert.Contains(cut, f => Is(f, "spk", "a", "stop"));
        var after = await puck.DrainAsync(TimeSpan.FromMilliseconds(1500));
        Assert.True(after.Count(f => f.IsAudio) <= 2, $"{after.Count(f => f.IsAudio)} audio frames after spk stop");
        Assert.DoesNotContain(after, f => Is(f, "spk", "a", "end") || (f.Json is { } m && IsAi(m, "idle")));
    }

    [Fact]
    public async Task A_tool_that_needs_confirmation_asks_on_the_puck()
    {
        Hub.Claude
            .Then(Reply("tool_use", [ToolUse("toolu_s1", "hub_tools_shutdown", "{}")])).Then(Text("Shutting it down."))
            .Then(Reply("tool_use", [ToolUse("toolu_s2", "hub_tools_shutdown", "{}")])).Then(Text("Okay, I won't."));
        await using var puck = await Hub.ConnectApprovedAsync();

        // Yes
        await puck.TalkAsync("start");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");
        var confirm = await puck.WaitForAsync("confirm");
        Assert.Equal("Shut down the test computer?", S(confirm, "text"));
        await puck.SendAsync(new JsonObject { ["t"] = "ans", ["id"] = S(confirm, "id"), ["ok"] = true });
        var rest = await puck.CollectUntilAsync(f => Is(f, "spk", "a", "end"));
        Assert.DoesNotContain(rest, f => f.Type == "confirm_end");   // answered on the puck: nothing to close
        Assert.Contains(rest, f => Is(f, "act", "s", "ok"));
        Assert.Equal([$"shutdown {puck.Id}"], Hub.Tools.Calls);
        await puck.SpeakerDoneAsync(rest.Last().Json!["id"]!.GetValue<int>());
        await puck.WaitForAsync("ai", m => IsAi(m, "idle"));

        // No
        await puck.TalkAsync("start");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");
        confirm = await puck.WaitForAsync("confirm");
        await puck.SendAsync(new JsonObject { ["t"] = "ans", ["id"] = S(confirm, "id"), ["ok"] = false });
        rest = await puck.CollectUntilAsync(f => Is(f, "spk", "a", "end"));
        Assert.Contains(rest, f => Is(f, "act", "s", "err"));
        Assert.Single(Hub.Tools.Calls);
        var refused = Hub.Claude.Requests[3].Messages.Last()!["content"]![0]!;
        Assert.True(refused["is_error"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Hearing_nothing_says_so_and_never_mind_skips_Claude()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        Hub.Speech.Transcript = "[BLANK_AUDIO]";
        var frames = await AskAsync(puck);
        Assert.Contains(frames, f => Is(f, "heard", "text", ""));
        Assert.Contains(frames, f => Is(f, "reply", "text", "Sorry, I didn't catch that."));
        await puck.SpeakerDoneAsync(frames.Last().Json!["id"]!.GetValue<int>());
        await puck.WaitForAsync("ai", m => IsAi(m, "idle"));

        Hub.Speech.Transcript = "Never mind.";
        frames = await AskAsync(puck);
        Assert.Contains(frames, f => Is(f, "reply", "text", "Okay."));
        await puck.SpeakerDoneAsync(frames.Last().Json!["id"]!.GetValue<int>());
        await puck.WaitForAsync("ai", m => IsAi(m, "idle"));
        Assert.Empty(Hub.Claude.Requests);
    }

    [Fact]
    public async Task A_failed_answer_is_shown_and_spoken_and_the_puck_is_left_showing_the_error()
    {
        Hub.Claude.Then(Error(401, "authentication_error", "invalid x-api-key"));
        await using var puck = await Hub.ConnectApprovedAsync();
        var frames = await AskAsync(puck);
        var error = frames.Single(f => f.Json is { } m && IsAi(m, "error")).Json!;
        Assert.Contains("API key was rejected", S(error, "text"));
        Assert.Contains("API key was rejected", string.Join(" ", Hub.Speech.Spoken));
        // Speaking the error put the puck in "speaking"; afterwards it must not be left there
        // (the puck stays awake while speaking): the hub puts the error back up
        await puck.SpeakerDoneAsync(frames.Last().Json!["id"]!.GetValue<int>());
        var after = await puck.WaitForAsync("ai", timeout: TimeSpan.FromSeconds(3));
        Assert.Equal("error", S(after, "s"));
    }

    [Fact]
    public async Task The_hub_notices_the_end_of_speech_by_itself()
    {
        await using var puck = await Hub.ConnectApprovedAsync();
        Hub.Claude.Then(Text("Yes."));
        await puck.TalkAsync("start");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        // the puck's own listen beep, a pause, speech, then silence; no talk stop
        var audio = Signals.Concat(Signals.Sine(1000, Rate, Rate / 10, 6000), Signals.Hiss(Rate * 3 / 10), Signals.Sine(300, Rate, Rate * 8 / 10, 2500), Signals.Hiss(Rate * 3 / 2));
        await puck.SendAudioAsync(audio);
        await puck.WaitForAsync("ai", m => IsAi(m, "thinking"), TimeSpan.FromSeconds(3));
        // The clip sent to the STT is trimmed to the speech (200 ms before, 300 ms after)
        var clip = await WaitForClipAsync();
        Assert.InRange(clip.Samples.Length, (int)(Rate * 1.1), (int)(Rate * 1.5));
    }

    async Task<Pucksley.Sdk.AudioClip> WaitForClipAsync()
    {
        await HubFixture.WaitUntilAsync(() => !Hub.Speech.Heard.IsEmpty, "the STT got a clip");
        Hub.Speech.Heard.TryPeek(out var clip);
        return clip!;
    }

    [Fact]
    public async Task A_puck_that_drops_mid_turn_cancels_the_turn_and_the_conversation_stays_whole()
    {
        Hub.Claude.Then(Hang());
        var puck = await Hub.ConnectApprovedAsync();
        await puck.TalkAsync("start");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");
        var request = await Hub.Claude.WaitForRequestsAsync(1);

        puck.Abort();   // Wi-Fi gone mid-answer
        await puck.DisposeAsync();
        Assert.True(await Task.WhenAny(request.Aborted.Task, Task.Delay(5000)) == request.Aborted.Task, "the Claude request should be cancelled");

        // It comes back: the next question works and the abandoned one left nothing in the conversation
        await using var back = await Hub.ConnectAsync(puck.Id);
        await back.HelloAsync(puck.Token);
        Assert.True((await back.WaitForAsync("welcome"))["ok"]!.GetValue<bool>());
        Hub.Claude.Then(Text("Hello again."));
        var frames = await AskAsync(back);
        Assert.Contains(frames.Concat(await back.DrainAsync(TimeSpan.FromMilliseconds(100))), f => Is(f, "reply", "text", "Hello again."));
        Assert.Single(Hub.Claude.Requests[1].Messages);
    }

    [Fact]
    public async Task A_second_link_for_the_same_puck_replaces_the_first()
    {
        // A puck that reconnects before the hub noticed its old link died (half-open TCP after a Wi-Fi drop)
        var old = await Hub.ConnectApprovedAsync();
        await using var fresh = await Hub.ConnectAsync(old.Id);
        await fresh.HelloAsync(old.Token);
        Assert.True((await fresh.WaitForAsync("welcome"))["ok"]!.GetValue<bool>());
        await fresh.WaitForAsync("state");

        // Everything now goes to the new link…
        await fresh.TalkAsync("start");
        await fresh.WaitForAsync("ai", m => IsAi(m, "listening"), TimeSpan.FromSeconds(3));
        await fresh.TalkAsync("cancel");
        await Hub.Tools.NotifyAsync("on the new link", old.Id);
        await fresh.WaitForAsync("notify", m => S(m, "text") == "on the new link", TimeSpan.FromSeconds(3));
        // …and the old one is closed
        Assert.True(await old.WaitClosedAsync(TimeSpan.FromSeconds(3)), "the stale link should be closed");
        await old.DisposeAsync();
        Assert.Single(Hub.Pucks.Session(old.Id)!.Connections);
    }
}
