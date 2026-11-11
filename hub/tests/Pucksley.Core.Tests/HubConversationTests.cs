using System.Text.Json.Nodes;
using Pucksley.Core.Pucks;
using Pucksley.Core.Tests.Support;
using static Pucksley.Core.Tests.Support.Claude;

namespace Pucksley.Core.Tests;

/// <summary>
/// Talking with the puck rather than at it: it listens for a reply after each answer, goes quiet
/// by itself, and (with a live engine) knows from the words when someone has finished.
/// </summary>
public sealed class HubConversationTests(HubFixture hub) : HubTestBase(hub)
{
    const int Rate = 16000;
    const string Sorry = "Sorry, I didn't catch that.";

    async Task UseLiveEngineAsync() =>
        (await Hub.PutJsonAsync("api/settings/voice", new JsonObject { ["sttEngine"] = "fake-live" })).EnsureSuccessStatusCode();

    /// <summary>A tapped question, answered and played out; returns what the puck got up to the end of playback.</summary>
    static async Task<List<PuckFrame>> AskAndPlayAsync(PuckClient puck, string via = "tap")
    {
        await puck.TalkAsync("start", via);
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");
        var frames = await puck.CollectUntilAsync(f => Is(f, "spk", "a", "end"));
        await puck.SpeakerDoneAsync(frames.Last().Json!["id"]!.GetValue<int>());
        return frames;
    }

    [Fact]
    public async Task Live_recognition_ends_the_turn_from_the_words_without_a_tap()
    {
        await UseLiveEngineAsync();
        Hub.LiveSpeech.Hear = n => n < Rate / 4 ? ("", false) : n < Rate / 2 ? ("what's the", false) : ("what's the time", true);
        Hub.Claude.Then(Text("It's ten past three."));
        await using var puck = await Hub.ConnectApprovedAsync();

        await puck.TalkAsync("start");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate));   // no "stop": the engine says when they've finished
        var frames = await puck.CollectUntilAsync(f => Is(f, "spk", "a", "end"));

        Assert.Contains(frames, f => Is(f, "heard", "text", "what's the time") && f.Json!["partial"] is null);
        Assert.EndsWith("what's the time", Hub.Claude.Requests[0].LastUserText());
        Assert.Equal(0, Hub.LiveSpeech.BatchCalls);   // the words were in already: no second transcription
        var stream = Assert.Single(Hub.LiveSpeech.Streams);
        Assert.True(stream.Finished);
        Assert.Equal(Hub.Settings.Current.Voice.EndSilenceMs, stream.Options.EndSilenceMs);
        Assert.Equal("en-NZ", stream.Options.Language);
    }

    [Fact]
    public async Task Without_live_recognition_the_recording_is_transcribed_as_before()
    {
        await UseLiveEngineAsync();
        Hub.LiveSpeech.FailToOpen = true;
        Hub.Claude.Then(Text("Sure."));
        await using var puck = await Hub.ConnectApprovedAsync();

        var frames = await AskAndPlayAsync(puck);

        Assert.Contains(frames, f => Is(f, "heard", "text", "from the recording"));
        Assert.Equal(1, Hub.LiveSpeech.BatchCalls);
    }

    [Fact]
    public async Task After_an_answer_it_listens_for_a_reply_and_goes_quiet_if_none_comes()
    {
        Hub.Settings.Update(s => s.Assistant.FollowUpSeconds = 2);
        Hub.Claude.Then(Text("The kitchen lights are on."));
        await using var puck = await Hub.ConnectApprovedAsync();

        await AskAndPlayAsync(puck);
        await puck.WaitForAsync("listen", timeout: TimeSpan.FromSeconds(2));
        await puck.TalkAsync("start", "hub");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));   // nothing said

        await puck.WaitForAsync("ai", m => IsAi(m, "idle"), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(Sorry, Hub.Speech.Spoken);   // no "didn't catch that" for a reply that never came
        Assert.Single(Hub.Claude.Requests);
    }

    [Fact]
    public async Task A_reply_that_cant_be_made_out_is_dropped_quietly()
    {
        Hub.Claude.Then(Text("The kitchen lights are on."));
        await using var puck = await Hub.ConnectApprovedAsync();
        await AskAndPlayAsync(puck);
        await puck.WaitForAsync("listen", timeout: TimeSpan.FromSeconds(2));

        Hub.Speech.Transcript = "";
        await puck.TalkAsync("start", "hub");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");

        await puck.WaitForAsync("ai", m => IsAi(m, "idle"), TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(Sorry, Hub.Speech.Spoken);
    }

    [Fact]
    public async Task A_question_it_cant_make_out_still_gets_a_sorry()
    {
        Hub.Speech.Transcript = "";
        await using var puck = await Hub.ConnectApprovedAsync();
        await puck.TalkAsync("start");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");
        await puck.CollectUntilAsync(f => Is(f, "spk", "a", "end"));
        Assert.Contains(Sorry, Hub.Speech.Spoken);
    }

    [Fact]
    public async Task Saying_thanks_ends_the_conversation()
    {
        Hub.Speech.Transcript = "Thanks!";
        Hub.Claude.Then(Text("You're welcome."));
        await using var puck = await Hub.ConnectApprovedAsync();

        await AskAndPlayAsync(puck);

        await puck.WaitForAsync("ai", m => IsAi(m, "idle"), TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(await puck.DrainAsync(TimeSpan.FromMilliseconds(200)), f => f.Type == "listen");
    }

    [Fact]
    public async Task It_can_be_told_to_listen_again_only_after_a_question()
    {
        Hub.Settings.Update(s => s.Assistant.FollowUpOnlyAfterQuestions = true);
        Hub.Claude.Then(Text("Done.")).Then(Text("Which room?"));
        await using var puck = await Hub.ConnectApprovedAsync();

        await AskAndPlayAsync(puck);
        await puck.WaitForAsync("ai", m => IsAi(m, "idle"), TimeSpan.FromSeconds(2));

        await AskAndPlayAsync(puck);
        await puck.WaitForAsync("listen", timeout: TimeSpan.FromSeconds(2));
    }

    /// <summary>A long answer the puck is still speaking (the fake puck never says it finished).</summary>
    async Task<PuckClient> SpeakingAsync()
    {
        Hub.Speech.SecondsPerSentence = 2;
        Hub.Claude.Then(Text("Tomorrow is fine and sunny in Auckland. The weekend brings rain on Saturday night."));
        var puck = await Hub.ConnectApprovedAsync();
        await puck.TalkAsync("start");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");
        await puck.WaitForAsync("ai", m => IsAi(m, "speaking"), TimeSpan.FromSeconds(5));
        return puck;
    }

    [Fact]
    public async Task Talking_over_it_stops_the_answer_and_what_was_said_is_answered()
    {
        await using var puck = await SpeakingAsync();
        Hub.Speech.Transcript = "what about Sunday";
        Hub.Claude.Then(Text("Sunday clears up."));

        await puck.TalkAsync("start", "voice");
        await puck.WaitForAsync("spk", m => S(m, "a") == "stop", TimeSpan.FromSeconds(2));   // it stops talking at once
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");
        await puck.WaitForAsync("heard", m => S(m, "text") == "what about Sunday", TimeSpan.FromSeconds(3));
        await puck.WaitForAsync("reply", m => S(m, "text") == "Sunday clears up.", TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Its_own_voice_heard_back_isnt_taken_for_someone_talking()
    {
        await using var puck = await SpeakingAsync();
        Hub.Speech.Transcript = "fine and sunny";   // from what it was saying

        await puck.TalkAsync("start", "voice");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");

        await puck.WaitForAsync("ai", m => IsAi(m, "idle"), TimeSpan.FromSeconds(3));
        Assert.Single(Hub.Claude.Requests);   // not asked about its own words
        Assert.DoesNotContain(Sorry, Hub.Speech.Spoken);
    }

    [Fact]
    public async Task Saying_stop_just_stops_it()
    {
        await using var puck = await SpeakingAsync();
        Hub.Speech.Transcript = "Stop.";

        await puck.TalkAsync("start", "voice");
        await puck.WaitForAsync("ai", m => IsAi(m, "listening"));
        await puck.SendAudioAsync(Signals.Hiss(Rate / 2));
        await puck.TalkAsync("stop");

        await puck.WaitForAsync("ai", m => IsAi(m, "idle"), TimeSpan.FromSeconds(3));
        Assert.Single(Hub.Claude.Requests);
    }

    [Theory]
    [InlineData("fine and sunny", "Tomorrow is fine and sunny in Auckland.", true)]
    [InlineData("Sunny in Auckland.", "Tomorrow is fine and sunny in Auckland.", true)]
    [InlineData("what about Sunday", "Tomorrow is fine and sunny in Auckland.", false)]
    [InlineData("is it sunny on Sunday", "Tomorrow is fine and sunny in Auckland.", false)]
    [InlineData("", "Tomorrow is fine.", false)]
    public void Its_own_words_are_recognised(string heard, string said, bool own) =>
        Assert.Equal(own, VoiceSession.IsOwnVoice(heard, said));

    [Theory]
    [InlineData("thanks", true)]
    [InlineData("thank you very much", true)]
    [InlineData("okay, thanks", true)]
    [InlineData("that's all", true)]
    [InlineData("that’s all", true)]
    [InlineData("goodbye", true)]
    [InlineData("ok", true)]
    [InlineData("thanks, and what about tomorrow", false)]
    [InlineData("what's the weather", false)]
    [InlineData("okay, turn it up", false)]
    public void Some_replies_are_the_end_of_the_conversation(string said, bool ends) =>
        Assert.Equal(ends, VoiceSession.EndsConversation(said));
}
