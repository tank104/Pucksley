using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pucksley.Core.Agent;
using Pucksley.Core.Settings;
using Pucksley.Core.Voice;
using Pucksley.Sdk;

namespace Pucksley.Core.Pucks;

/// <summary>
/// One puck's conversation loop: listen → transcribe → ask Claude (streamed) → speak it sentence
/// by sentence on the puck's speaker → listen again for a reply (or idle). A tap while it's
/// thinking or speaking interrupts.
/// </summary>
/// <remarks>
/// With a live engine (<see cref="IStreamingSpeechToText"/>) the words are recognised while the
/// person talks, and the engine says when they've finished; otherwise the mic audio is recorded,
/// a loudness detector notices the end, and the recording is transcribed.
/// </remarks>
public sealed class VoiceSession
{
    enum State { Idle, Listening, Thinking, Speaking }

    const int Rate = SpeechService.Rate;
    const int LeadMs = 700;          // how far ahead of the speaker the hub sends audio
    const int FrameSamples = 640;    // 40 ms per audio frame to the puck

    readonly PuckSession _puck;
    readonly AgentService _agent;
    readonly SpeechService _speech;
    readonly SettingsStore _settings;
    readonly ILogger _log;
    readonly object _gate = new();

    State _state = State.Idle;
    readonly List<short> _audio = new(Rate * 10);
    VoiceActivityDetector _vad = new();
    readonly Stopwatch _listenClock = new();
    string _listenVia = "tap";
    CancellationTokenSource? _turn;
    int _spkId;
    TaskCompletionSource? _playbackDone;
    int _awaitingSpkId = -1;

    // Live recognition of the current listen (null: none, or it hasn't connected yet)
    ISpeechStream? _stream;
    int _listenGen;                  // which listen a stream that connects late belongs to
    bool _followUp;                  // this listen is the puck carrying on after an answer
    string _shownWords = "";
    long _shownWordsAt;

    // What the puck has been saying this reply (to tell its own voice, heard back, from someone
    // talking over it), and what it was saying when someone did
    readonly StringBuilder _spoken = new();
    string? _talkedOver;

    public VoiceSession(PuckSession puck, AgentService agent, SpeechService speech, SettingsStore settings, ILogger log)
    {
        _puck = puck;
        _agent = agent;
        _speech = speech;
        _settings = settings;
        _log = log;
    }

    public string StateName { get { lock (_gate) return _state.ToString().ToLowerInvariant(); } }

    // ------------------------------------------------------------------ from the puck

    public void OnTalk(string action, string? via)
    {
        switch (action)
        {
            case "start": StartListening(via ?? "tap"); break;
            case "stop": StopListening("puck"); break;
            case "cancel": Cancel(); break;
        }
    }

    public void OnAudio(short[] samples)
    {
        bool end = false;
        lock (_gate)
        {
            if (_state != State.Listening) return;
            var v = _settings.Current.Voice;
            int max = Math.Clamp(v.MaxListenSeconds, 5, 120) * Rate;
            int room = max - _audio.Count;
            if (room <= 0) end = true;
            else
            {
                var take = samples.Length <= room ? samples : samples[..room];
                _audio.AddRange(take);
                _vad.Process(take);
                _stream?.Send(take);
                // With live recognition the engine says when they've finished (OnWords, Tick)
                if (v.AutoEndOfSpeech && _vad.SpeechEnded && !LiveLocked) end = true;
                if (_audio.Count >= max) end = true;
            }
        }
        if (end) StopListening("silence");
    }

    public void OnSpeakerDone(int id)
    {
        lock (_gate)
        {
            if (id == _awaitingSpkId) _playbackDone?.TrySetResult();
        }
    }

    /// <summary>Extra seconds to start talking when the puck was lifted (the firmware's direct mode uses the same).</summary>
    internal const double LiftGraceSeconds = 8;

    /// <summary>About four times a second: listening timeouts.</summary>
    public void Tick()
    {
        bool giveUp = false, endNow = false;
        lock (_gate)
        {
            if (_state != State.Listening) return;
            var v = _settings.Current.Voice;
            // Carrying on after an answer: a few seconds for them to reply, then quietly stop.
            // Holding the puck up to talk: give them longer to start (it could also have been picked
            // up just to move it, so don't wait forever and leave it listening in someone's bag).
            double limit = _followUp
                ? Math.Clamp(_settings.Current.Assistant.FollowUpSeconds, 2, 30)
                : Math.Clamp(v.NoSpeechTimeoutSeconds, 3, 30) + (_listenVia == "lift" ? LiftGraceSeconds : 0);
            double elapsed = _listenClock.Elapsed.TotalSeconds;
            if (LiveLocked)
            {
                bool words = _stream!.Text.Length > 0;
                // Sound right now (words lag it a little): don't cut someone off as they start
                bool soundNow = _vad.SpeechStarted && _audio.Count - _vad.LastSpeechSample < Rate;
                if (!words && elapsed > limit && (!soundNow || elapsed > limit + 4)) giveUp = true;
                // Words, but no word from the engine that they've finished (noise after them...):
                // a long quiet after the last loud moment is enough
                else if (words && _vad.SpeechEnded && _audio.Count - _vad.LastSpeechSample > Rate * 5 / 2) endNow = true;
            }
            else if (!_vad.SpeechStarted && elapsed > limit) giveUp = true;

            // No words, but there was sound and they asked (a tap): try what was heard, so a
            // mumble gets a "didn't catch that" rather than silence
            if (giveUp && !_followUp && _vad.SpeechStarted)
            {
                giveUp = false;
                endNow = true;
            }
            if (giveUp)
            {
                _state = State.Idle;
                DropStreamLocked();
                _audio.Clear();
            }
        }
        if (endNow)
        {
            StopListening("no clear words");
            return;
        }
        if (giveUp)
        {
            _log.LogInformation("{Puck}: heard nothing, stopped listening", _puck.Name);
            _puck.Send(new JsonObject { ["t"] = "ai", ["s"] = "idle" });
        }
    }

    /// <summary>Live recognition is going (connected and working) for this listen.</summary>
    bool LiveLocked => _stream is { Failed: false };

    void DropStreamLocked()
    {
        var stream = _stream;
        _stream = null;
        if (stream is not null) _ = stream.DisposeAsync().AsTask();
    }

    /// <summary>The puck went away: stop whatever was going on.</summary>
    public void OnDisconnected()
    {
        lock (_gate)
        {
            _turn?.Cancel();
            _state = State.Idle;
            DropStreamLocked();
            _playbackDone?.TrySetResult();
        }
    }

    // ------------------------------------------------------------------ listening

    void StartListening(string via)
    {
        int gen;
        var v = _settings.Current.Voice;
        lock (_gate)
        {
            // Talked over: remember what it was saying, in case what's heard is only that
            _talkedOver = via == "voice" ? _spoken.ToString() : null;
            if (_state is State.Thinking or State.Speaking) InterruptLocked();
            DropStreamLocked();
            _state = State.Listening;
            _audio.Clear();
            _vad = new VoiceActivityDetector(v.VadSensitivity, v.EndSilenceMs);
            _listenVia = via;
            // The hub asked the puck to listen (carrying on after an answer), or the person talked
            // over it ("voice"): if nothing clear is said, quietly stop rather than say sorry
            _followUp = via is "hub" or "voice";
            _shownWords = "";
            _listenClock.Restart();
            gen = ++_listenGen;
        }
        _puck.Send(new JsonObject { ["t"] = "ai", ["s"] = "listening" });
        if (v.LiveRecognition && _speech.Stt is IStreamingSpeechToText live) _ = OpenStreamAsync(live, gen);
    }

    /// <summary>Connects live recognition for listen <paramref name="gen"/>; meanwhile the audio is kept, and sent once it's up.</summary>
    async Task OpenStreamAsync(IStreamingSpeechToText engine, int gen)
    {
        ISpeechStream? stream = null;
        var started = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var v = _settings.Current.Voice;
            var lang = string.IsNullOrWhiteSpace(v.Language) ? null : v.Language.Trim();
            stream = await engine.StartStreamAsync(new SpeechStreamOptions(lang, v.EndSilenceMs), cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogInformation("{Puck}: live recognition unavailable ({Message}); recording instead", _puck.Name, e.Message);
        }
        if (stream is null) return;
        bool keep;
        lock (_gate)
        {
            keep = _state == State.Listening && _listenGen == gen && _stream is null;
            if (keep)
            {
                _stream = stream;
                stream.Changed += () => OnWords(stream);
                // What was heard while it connected
                if (_audio.Count > 0) stream.Send(CollectionsMarshal.AsSpan(_audio));
            }
        }
        if (!keep) await stream.DisposeAsync().ConfigureAwait(false);
        else _log.LogDebug("{Puck}: live recognition up in {Ms} ms", _puck.Name, started.ElapsedMilliseconds);
    }

    /// <summary>Live recognition heard more (or finished, or failed).</summary>
    void OnWords(ISpeechStream stream)
    {
        bool end = false;
        string? show = null;
        lock (_gate)
        {
            if (_state != State.Listening || !ReferenceEquals(_stream, stream)) return;
            if (stream.Ended) end = true;
            // The words so far, for the screen (a few times a second at most)
            var text = stream.Text;
            long now = _listenClock.ElapsedMilliseconds;
            if (!end && text.Length > 0 && text != _shownWords && now - _shownWordsAt >= 150)
            {
                _shownWords = text;
                _shownWordsAt = now;
                show = text;
            }
        }
        if (show is not null) _puck.Send(new JsonObject { ["t"] = "heard", ["text"] = Clip(show, 300), ["partial"] = true });
        if (end) StopListening("finished");
    }

    void StopListening(string why)
    {
        short[] clip;
        ISpeechStream? stream;
        bool followUp, heardSound;
        string? talkedOver;
        lock (_gate)
        {
            if (_state != State.Listening) return;
            _state = State.Thinking;
            stream = _stream;
            _stream = null;
            followUp = _followUp;
            heardSound = _vad.SpeechStarted;
            talkedOver = _talkedOver;
            // Trim silence before and after the speech (keeps transcription quick and avoids
            // engines hallucinating words into silence); a tap with no detected speech keeps it all.
            int start = 0, end = _audio.Count;
            if (_vad.SpeechStarted)
            {
                start = Math.Clamp(_vad.SpeechStartSample, 0, _audio.Count);
                end = Math.Clamp(_vad.LastSpeechSample + Rate * 3 / 10, start, _audio.Count);
            }
            clip = _audio.GetRange(start, end - start).ToArray();
            _audio.Clear();
            _turn?.Cancel();
            _turn = new CancellationTokenSource();
        }
        _puck.Send(new JsonObject { ["t"] = "ai", ["s"] = "thinking" });
        _log.LogInformation("{Puck}: heard {Seconds:0.0} s ({Why}{Live})", _puck.Name, clip.Length / (double)Rate, why,
            stream is { Failed: false } ? ", live" : "");
        var cts = _turn;
        var heard = new Heard(clip, stream, followUp, heardSound, talkedOver);
        _ = Task.Run(() => RunTurnAsync(heard, cts.Token));
    }

    /// <summary>What a listen brought back: the recording, the live recognition (if any), and how it came about.</summary>
    sealed record Heard(short[] Clip, ISpeechStream? Stream, bool FollowUp, bool Sound, string? TalkedOver);

    void Cancel()
    {
        lock (_gate)
        {
            InterruptLocked();
            DropStreamLocked();
            _state = State.Idle;
            _audio.Clear();
        }
        _puck.Send(new JsonObject { ["t"] = "ai", ["s"] = "idle" });
    }

    void InterruptLocked()
    {
        _turn?.Cancel();
        _turn = null;
        if (_state == State.Speaking || _awaitingSpkId >= 0)
            _puck.Send(new JsonObject { ["t"] = "spk", ["a"] = "stop" });
        _playbackDone?.TrySetResult();
    }

    // ------------------------------------------------------------------ a turn

    async Task RunTurnAsync(Heard heard, CancellationToken cancel)
    {
        var clip = heard.Clip;
        try
        {
            string? transcript = null;
            var sw = Stopwatch.StartNew();
            bool live = false;
            // Live: the words are in already; the engine only has to finish the last ones
            if (heard.Stream is { } stream)
            {
                await using (stream.ConfigureAwait(false))
                {
                    if (!stream.Failed)
                    {
                        try
                        {
                            transcript = await stream.FinishAsync(cancel).ConfigureAwait(false);
                            live = true;
                        }
                        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
                        {
                            _log.LogInformation("{Puck}: live recognition didn't finish ({Message}); transcribing the recording", _puck.Name, e.Message);
                        }
                    }
                }
            }

            if (transcript is null && clip.Length < Rate / 4)
            {
                await NothingHeardAsync(heard, cancel).ConfigureAwait(false);
                return;
            }

            if (transcript is null)
            using (var sttTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                sttTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    transcript = await _speech.TranscribeAsync(clip, sttTimeout.Token).ConfigureAwait(false);
                }
                catch (PluginException e)
                {
                    await FailAsync(e.Message, cancel).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    await FailAsync("Speech recognition took too long.", cancel).ConfigureAwait(false);
                    return;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogWarning(e, "Transcription failed");
                    await FailAsync($"Speech recognition failed: {e.Message}", cancel).ConfigureAwait(false);
                    return;
                }
            }
            _log.LogInformation("{Puck} heard \"{Text}\" ({Ms} ms{Live})", _puck.Name, transcript, sw.ElapsedMilliseconds, live ? " after they stopped, live" : "");
            if (string.IsNullOrWhiteSpace(transcript))
            {
                await NothingHeardAsync(heard, cancel).ConfigureAwait(false);
                return;
            }
            _puck.Send(new JsonObject { ["t"] = "heard", ["text"] = Clip(transcript, 300) });

            var lower = transcript.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
            // Talked over, but what was heard is the puck's own voice coming back: nobody spoke
            if (heard.TalkedOver is { } said && IsOwnVoice(transcript, said))
            {
                _log.LogInformation("{Puck}: that was its own voice, not someone talking over it", _puck.Name);
                FinishTurn(false);
                return;
            }
            // "Stop", "wait": it has stopped talking; nothing to answer
            if (lower is "stop" or "stop it" or "stop talking" or "wait" or "hang on" or "hold on" or "shush" or "shh" or "quiet"
                or "be quiet" or "enough" or "ok stop" or "okay stop" or "pause")
            {
                FinishTurn(false);
                return;
            }
            if (lower is "never mind" or "nevermind" or "cancel" or "forget it" or "nothing")
            {
                await SpeakOnlyAsync("Okay.", cancel).ConfigureAwait(false);
                FinishTurn(false);
                return;
            }
            if (lower is "start over" or "new conversation" or "forget that" or "let's start again")
            {
                _agent.Reset(_puck.SessionKey);
                await SpeakOnlyAsync("Okay, fresh start.", cancel).ConfigureAwait(false);
                FinishTurn(false);
                return;
            }

            await AnswerAsync(transcript, EndsConversation(lower), cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // interrupted (a tap, or the puck went away)
        }
        catch (Exception e)
        {
            _log.LogError(e, "Voice turn failed");
            try { await FailAsync("Something went wrong on the hub.", CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Whether <paramref name="heard"/> is just some of what the puck was saying (<paramref name="said"/>),
    /// heard back through its own mic: a few words, nearly all of them from what it said.
    /// </summary>
    internal static bool IsOwnVoice(string heard, string said)
    {
        static string[] Words(string t) => System.Text.RegularExpressions.Regex.Matches(t.ToLowerInvariant(), @"[\p{L}\p{N}']+")
            .Select(m => m.Value.Trim('\'')).Where(w => w.Length > 0).ToArray();
        var h = Words(heard);
        if (h.Length is 0 or > 8) return false;
        var s = Words(said).ToHashSet();
        int found = h.Count(s.Contains);
        return found >= Math.Max(1, (int)Math.Ceiling(h.Length * 0.7));
    }

    /// <summary>
    /// "Thanks", "that's all", "bye": the person is done, so after the answer the puck doesn't
    /// listen for more. <paramref name="lower"/> is lower case.
    /// </summary>
    internal static bool EndsConversation(string lower)
    {
        var t = lower.Trim().TrimEnd('.', '!', '?', ',').Replace('\u2019', '\'');
        foreach (var lead in new[] { "ok, ", "okay, ", "ok ", "okay ", "great, ", "great ", "perfect, ", "perfect ", "cool, ", "cool ", "awesome, ", "awesome " })
            if (t.StartsWith(lead, StringComparison.Ordinal)) t = t[lead.Length..].Trim();
        foreach (var tail in new[] { " very much", " so much", " mate", " heaps", " a lot" })
            if (t.EndsWith(tail, StringComparison.Ordinal)) t = t[..^tail.Length].Trim();
        return t is "thanks" or "thank you" or "thankyou" or "cheers" or "ta" or "that's all" or "that's it" or "that is all"
            or "that's everything" or "that'll do" or "bye" or "goodbye" or "good bye" or "bye bye" or "see you" or "good night"
            or "goodnight" or "no thanks" or "no thank you" or "nothing else" or "all good" or "i'm done" or "we're done"
            or "stop listening" or "go to sleep" or "ok" or "okay" or "great" or "perfect" or "cool" or "awesome";
    }

    async Task AnswerAsync(string transcript, bool lastWord, CancellationToken cancel)
    {
        var s = _settings.Current;
        var reply = new StringBuilder();
        var splitter = new SentenceSplitter();
        var sentences = Channel.CreateUnbounded<string>();
        long lastReplySent = 0;
        bool filled = false;   // said "let me check" (or similar) already this turn
        var replyClock = Stopwatch.StartNew();

        var speaker = SpeakLoopAsync(sentences.Reader, cancel);

        var callbacks = new AgentCallbacks
        {
            OnText = delta =>
            {
                lock (reply) reply.Append(delta);
                foreach (var sentence in splitter.Add(delta)) sentences.Writer.TryWrite(sentence);
                if (replyClock.ElapsedMilliseconds - lastReplySent > 150)
                {
                    lastReplySent = replyClock.ElapsedMilliseconds;
                    string text;
                    lock (reply) text = reply.ToString();
                    _puck.Send(new JsonObject { ["t"] = "reply", ["text"] = Clip(text.Trim(), 700), ["done"] = false });
                }
            },
            OnActivity = (icon, text, state) =>
                _puck.Send(new JsonObject { ["t"] = "act", ["icon"] = icon, ["text"] = Clip(text, 60), ["s"] = state }),
            Confirm = question => _puck.ConfirmAsync(question, TimeSpan.FromSeconds(30), cancel),
            // Something slow before any answer (a web search, the bigger model): say so, once, so the wait isn't silent
            OnToolStarted = name =>
            {
                var line = name switch
                {
                    "web_search" or "web_fetch" => "Let me check.",
                    AgentService.ThinkHarder => "Let me think about that.",
                    _ => null,
                };
                if (line is null) return;
                lock (reply)
                {
                    if (filled || reply.ToString().Trim().Length > 0) return;
                    filled = true;
                }
                sentences.Writer.TryWrite(line);
            },
        };

        var result = await _agent.AskAsync(_puck.SessionKey, transcript, _puck.Caller, callbacks, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();

        if (result.Failed)
        {
            sentences.Writer.TryComplete();
            try { await speaker.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await FailAsync(result.Text, cancel).ConfigureAwait(false);
            return;
        }

        if (splitter.Flush() is { } rest) sentences.Writer.TryWrite(rest);
        sentences.Writer.TryComplete();
        _puck.Send(new JsonObject { ["t"] = "reply", ["text"] = Clip(result.Text, 700), ["done"] = true });

        await speaker.ConfigureAwait(false);
        // Carry on listening for a reply, unless they said that was all (or there's no voice: the
        // answer is on the screen to read, which takes longer than a reply would wait)
        bool followUp = s.Assistant.FollowUpListening && !lastWord && _speech.Tts is not null &&
                        (!s.Assistant.FollowUpOnlyAfterQuestions || EndsWithQuestion(result.Text));
        FinishTurn(followUp);
    }

    static bool EndsWithQuestion(string text)
    {
        var t = text.TrimEnd();
        return t.EndsWith('?') || t.EndsWith("?\"", StringComparison.Ordinal) || t.EndsWith("?)", StringComparison.Ordinal);
    }

    void FinishTurn(bool listenAgain)
    {
        lock (_gate)
        {
            if (_state is State.Listening) return; // the person already tapped
            _state = State.Idle;
        }
        if (listenAgain) _puck.Send(new JsonObject { ["t"] = "listen" });
        else _puck.Send(new JsonObject { ["t"] = "ai", ["s"] = "idle" });
    }

    async Task NothingHeardAsync(Heard heard, CancellationToken cancel)
    {
        _puck.Send(new JsonObject { ["t"] = "heard", ["text"] = "" });
        // Carrying on after an answer and nothing (clear) was said: they've finished, quietly stop
        if (heard.FollowUp)
        {
            FinishTurn(false);
            return;
        }
        await SpeakOnlyAsync("Sorry, I didn't catch that.", cancel, showText: true).ConfigureAwait(false);
        FinishTurn(false);
    }

    async Task FailAsync(string message, CancellationToken cancel)
    {
        _log.LogWarning("{Puck}: {Message}", _puck.Name, message);
        var error = new JsonObject { ["t"] = "ai", ["s"] = "error", ["text"] = Clip(message, 200) };
        _puck.Send(error);
        try { await SpeakOnlyAsync(message, cancel, showText: false).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { }
        bool restore;
        lock (_gate)
        {
            restore = _state != State.Listening;
            if (restore) _state = State.Idle;
        }
        // Speaking it switched the puck to "speaking" (which keeps its screen awake, waiting for
        // more): put the error back up once it has been said (a repeat is harmless if it wasn't)
        if (restore) _puck.Send(error.DeepClone().AsObject());
    }

    /// <summary>Speaks a fixed line (no Claude), e.g. a reminder or "Sorry, I didn't catch that".</summary>
    async Task SpeakOnlyAsync(string text, CancellationToken cancel, bool showText = true)
    {
        if (showText) _puck.Send(new JsonObject { ["t"] = "reply", ["text"] = Clip(text, 700), ["done"] = true });
        var ch = Channel.CreateUnbounded<string>();
        ch.Writer.TryWrite(text);
        ch.Writer.TryComplete();
        await SpeakLoopAsync(ch.Reader, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Announcements from plugins (reminders, timers): spoken when the puck isn't busy (waits up
    /// to 20 s for it to finish a conversation).
    /// </summary>
    public async Task SayAsync(string text, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        CancellationTokenSource turn;
        while (true)
        {
            lock (_gate)
            {
                if (_state == State.Idle)
                {
                    // Its own turn, so a tap (talk start) or the puck going away stops it like an answer
                    turn = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    _turn = turn;
                    _state = State.Speaking;
                    break;
                }
            }
            if (DateTime.UtcNow > deadline) return;
            await Task.Delay(500, cancel).ConfigureAwait(false);
        }
        try
        {
            await SpeakOnlyAsync(text, turn.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            // interrupted on the puck (or it went away): nothing for the plugin to handle
        }
        finally
        {
            bool idle = false;
            lock (_gate)
            {
                if (ReferenceEquals(_turn, turn)) _turn = null;
                if (_state == State.Speaking)
                {
                    _state = State.Idle;
                    idle = true;
                }
            }
            turn.Dispose();
            if (idle) _puck.Send(new JsonObject { ["t"] = "ai", ["s"] = "idle" });
        }
    }

    /// <summary>
    /// Turns sentences into speech and streams it to the puck, paced a little ahead of real time.
    /// Synthesis of the next sentence overlaps playback of the current one.
    /// </summary>
    void NoteSpoken(string sentence)
    {
        lock (_gate)
        {
            if (_spoken.Length > 4000) _spoken.Remove(0, 2000);
            _spoken.Append(' ').Append(sentence);
        }
    }

    async Task SpeakLoopAsync(ChannelReader<string> sentences, CancellationToken cancel)
    {
        lock (_gate) _spoken.Clear();
        // A talking face (Openspline…) on a puck that can show it: the reply goes through the face
        if (_speech.Face is { } face && _puck.CanShowVideo)
        {
            ITalkingFaceTurn? turn = null;
            try { turn = await face.BeginTurnAsync(cancel).ConfigureAwait(false); }
            catch (PluginException e) { _log.LogInformation("Talking face unavailable, speaking without it: {Message}", e.Message); }
            if (turn is not null)
            {
                await using (turn.ConfigureAwait(false))
                    await SpeakWithFaceAsync(sentences, turn, cancel).ConfigureAwait(false);
                return;
            }
        }

        int id = -1;
        var clock = Stopwatch.StartNew();
        double bufferedUntilMs = 0;   // when the puck will run out of audio, on `clock`
        try
        {
            await foreach (var sentence in sentences.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                NoteSpoken(sentence);
                IAsyncEnumerable<short[]> audio;
                try { audio = _speech.SynthesizeAsync(sentence, cancel); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogWarning(e, "Speech synthesis failed");
                    continue;
                }
                try
                {
                    await foreach (var chunk in audio.ConfigureAwait(false))
                    {
                        if (id < 0)
                        {
                            lock (_gate)
                            {
                                id = ++_spkId;
                                _awaitingSpkId = id;
                                _playbackDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                if (_state != State.Listening) _state = State.Speaking;
                            }
                            _puck.Send(new JsonObject { ["t"] = "spk", ["a"] = "start", ["id"] = id });
                            _puck.Send(new JsonObject { ["t"] = "ai", ["s"] = "speaking" });
                        }
                        for (int off = 0; off < chunk.Length; off += FrameSamples)
                        {
                            int n = Math.Min(FrameSamples, chunk.Length - off);
                            double now = clock.Elapsed.TotalMilliseconds;
                            double ahead = bufferedUntilMs - now;
                            if (ahead > LeadMs) await Task.Delay(TimeSpan.FromMilliseconds(ahead - LeadMs), cancel).ConfigureAwait(false);
                            now = clock.Elapsed.TotalMilliseconds;
                            bufferedUntilMs = Math.Max(bufferedUntilMs, now) + n * 1000.0 / Rate;
                            _puck.SendAudio(chunk.AsSpan(off, n));
                        }
                    }
                }
                catch (PluginException e)
                {
                    _log.LogWarning("Speech synthesis failed: {Message}", e.Message);
                }
                catch (HttpRequestException e)
                {
                    _log.LogWarning("Speech synthesis failed: {Message}", e.Message);
                }
            }

            if (id >= 0)
            {
                _puck.Send(new JsonObject { ["t"] = "spk", ["a"] = "end", ["id"] = id });
                // Wait for the puck to finish playing (it says so), or for the audio to have run out
                var remaining = Math.Max(0, bufferedUntilMs - clock.Elapsed.TotalMilliseconds);
                var done = _playbackDone?.Task ?? Task.CompletedTask;
                await Task.WhenAny(done, Task.Delay(TimeSpan.FromMilliseconds(remaining + 2500), cancel)).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            lock (_gate)
            {
                if (id >= 0 && _awaitingSpkId == id) _awaitingSpkId = -1;
            }
        }
    }

    /// <summary>
    /// A reply through the talking face: speech goes to the face service as it's synthesised;
    /// what comes back (a frame and its audio, 40 ms at a time) streams to the puck, the frame
    /// stamped with where its audio starts so the puck shows it as that audio plays. If the face
    /// sends nothing back within a few seconds, the reply plays as plain audio instead.
    /// </summary>
    async Task SpeakWithFaceAsync(ChannelReader<string> sentences, ITalkingFaceTurn face, CancellationToken cancel)
    {
        const int NoFaceAfterMs = 6000;
        int id = -1;
        var clock = Stopwatch.StartNew();
        double bufferedUntilMs = 0, firstSentMs = -1, spokenMs = 0;
        uint sent = 0;                                   // samples of this utterance sent to the puck
        var spoken = new List<short[]>();                // the whole reply, in case the face fails
        var gotMedia = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool faceFailed = false;
        int frames = 0;
        double firstSpeechMs = -1, firstMediaMs = -1, firstAudioOutMs = -1, endSentMs = -1, oneSecondSentMs = -1;
        long sentToFace = 0;
        using var faceCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        void Start()
        {
            if (id >= 0) return;
            lock (_gate)
            {
                id = ++_spkId;
                _awaitingSpkId = id;
                _playbackDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_state != State.Listening) _state = State.Speaking;
            }
            _puck.Send(new JsonObject { ["t"] = "spk", ["a"] = "start", ["id"] = id });
            _puck.Send(new JsonObject { ["t"] = "ai", ["s"] = "speaking" });
        }

        async Task PlayAsync(short[] pcm, CancellationToken token)
        {
            for (int off = 0; off < pcm.Length; off += FrameSamples)
            {
                int n = Math.Min(FrameSamples, pcm.Length - off);
                double ahead = bufferedUntilMs - clock.Elapsed.TotalMilliseconds;
                if (ahead > LeadMs) await Task.Delay(TimeSpan.FromMilliseconds(ahead - LeadMs), token).ConfigureAwait(false);
                bufferedUntilMs = Math.Max(bufferedUntilMs, clock.Elapsed.TotalMilliseconds) + n * 1000.0 / Rate;
                _puck.SendAudio(pcm.AsSpan(off, n));
                sent += (uint)n;
            }
        }

        var forward = Task.Run(async () =>
        {
            var decimate = new Decimator();
            await foreach (var media in face.ReadAsync(faceCts.Token).ConfigureAwait(false))
            {
                if (firstMediaMs < 0) firstMediaMs = clock.Elapsed.TotalMilliseconds;
                gotMedia.TrySetResult();
                Start();
                var pcm = media.SampleRate == Rate ? media.Audio
                    : media.SampleRate == Rate * Decimator.Factor ? decimate.Process(media.Audio)
                    : throw new PluginException($"The face sent {media.SampleRate} Hz audio");
                if (media.Jpeg.Length > 0 && _puck.SendVideoFrame(media.Jpeg, sent)) frames++;
                if (firstAudioOutMs < 0) firstAudioOutMs = clock.Elapsed.TotalMilliseconds;
                await PlayAsync(pcm, faceCts.Token).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var sentence in sentences.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                NoteSpoken(sentence);
                IAsyncEnumerable<short[]> audio;
                try { audio = _speech.SynthesizeAsync(sentence, cancel); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogWarning(e, "Speech synthesis failed");
                    continue;
                }
                try
                {
                    await foreach (var chunk in audio.ConfigureAwait(false))
                    {
                        spoken.Add(chunk);
                        spokenMs += chunk.Length * 1000.0 / Rate;
                        if (firstSpeechMs < 0) firstSpeechMs = clock.Elapsed.TotalMilliseconds;
                        if (faceFailed) continue;
                        try
                        {
                            await face.SendAudioAsync(chunk, Rate, cancel).ConfigureAwait(false);
                            if (firstSentMs < 0) firstSentMs = clock.Elapsed.TotalMilliseconds;
                            sentToFace += chunk.Length;
                            if (oneSecondSentMs < 0 && sentToFace >= Rate) oneSecondSentMs = clock.Elapsed.TotalMilliseconds;
                        }
                        catch (PluginException e)
                        {
                            _log.LogWarning("Talking face failed: {Message}", e.Message);
                            faceFailed = true;
                        }
                        if (!gotMedia.Task.IsCompleted && firstSentMs >= 0 && clock.Elapsed.TotalMilliseconds - firstSentMs > NoFaceAfterMs) faceFailed = true;
                    }
                }
                catch (Exception e) when (e is PluginException or HttpRequestException)
                {
                    _log.LogWarning("Speech synthesis failed: {Message}", e.Message);
                }
            }

            endSentMs = clock.Elapsed.TotalMilliseconds;
            if (!faceFailed)
            {
                try { await face.EndAsync(cancel).ConfigureAwait(false); }
                catch (PluginException e)
                {
                    _log.LogWarning("Talking face failed: {Message}", e.Message);
                    faceFailed = true;
                }
            }
            if (!faceFailed && !gotMedia.Task.IsCompleted)
            {
                var first = await Task.WhenAny(gotMedia.Task, Task.Delay(NoFaceAfterMs, cancel)).ConfigureAwait(false);
                if (first != gotMedia.Task) faceFailed = true;
            }

            if (!gotMedia.Task.IsCompleted)
            {
                // Nothing came back from the face: say it plainly
                if (spoken.Count > 0) _log.LogWarning("Talking face sent nothing back; speaking without it");
                await face.InterruptAsync().ConfigureAwait(false);
                faceCts.Cancel();
                foreach (var chunk in spoken)
                {
                    Start();
                    await PlayAsync(chunk, cancel).ConfigureAwait(false);
                }
            }
            else
            {
                // The face plays out the rest of the reply (bounded, in case the service stalls)
                var limit = TimeSpan.FromMilliseconds(spokenMs + 10000);
                if (await Task.WhenAny(forward, Task.Delay(limit, cancel)).ConfigureAwait(false) != forward)
                    _log.LogWarning("Talking face stalled; ending the reply");
                cancel.ThrowIfCancellationRequested();
            }

            if (id >= 0)
            {
                _puck.Send(new JsonObject { ["t"] = "spk", ["a"] = "end", ["id"] = id });
                _puck.Send(new JsonObject { ["t"] = "fv", ["a"] = "end" });   // the talking face is done: back to the usual face
                _log.LogInformation("Talking face: {Frames} frames for {Seconds:0.0} s of speech; first speech {Speech:0} ms, a second of it at the face {OneSecond:0} ms, all speech sent {End:0} ms, first frame {Media:0} ms, first audio to the puck {Out:0} ms",
                    frames, sent / (double)Rate, firstSpeechMs, oneSecondSentMs, endSentMs, firstMediaMs, firstAudioOutMs);
                var remaining = Math.Max(0, bufferedUntilMs - clock.Elapsed.TotalMilliseconds);
                var done = _playbackDone?.Task ?? Task.CompletedTask;
                await Task.WhenAny(done, Task.Delay(TimeSpan.FromMilliseconds(remaining + 2500), cancel)).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            await face.InterruptAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            faceCts.Cancel();
            try { await forward.ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or PluginException) { }
            lock (_gate)
            {
                if (id >= 0 && _awaitingSpkId == id) _awaitingSpkId = -1;
            }
        }
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
