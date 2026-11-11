using System.Diagnostics;
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
/// One puck's conversation loop: listen (mic audio + end-of-speech detection) → transcribe → ask
/// Claude (streamed) → speak it sentence by sentence on the puck's speaker → idle, or listen again
/// when Claude asked a question. A tap while it's thinking or speaking interrupts.
/// </summary>
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
                if (v.AutoEndOfSpeech && _vad.SpeechEnded) end = true;
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
        bool giveUp = false;
        lock (_gate)
        {
            if (_state != State.Listening) return;
            var v = _settings.Current.Voice;
            // Holding the puck up to talk: give them longer to start (it could also have been picked
            // up just to move it, so don't wait forever and leave it listening in someone's bag)
            double limit = Math.Clamp(v.NoSpeechTimeoutSeconds, 3, 30) + (_listenVia == "lift" ? LiftGraceSeconds : 0);
            if (!_vad.SpeechStarted && _listenClock.Elapsed.TotalSeconds > limit) giveUp = true;
        }
        if (giveUp)
        {
            _log.LogInformation("{Puck}: heard nothing, stopped listening", _puck.Name);
            lock (_gate) _state = State.Idle;
            _puck.Send(new JsonObject { ["t"] = "ai", ["s"] = "idle" });
        }
    }

    /// <summary>The puck went away: stop whatever was going on.</summary>
    public void OnDisconnected()
    {
        lock (_gate)
        {
            _turn?.Cancel();
            _state = State.Idle;
            _playbackDone?.TrySetResult();
        }
    }

    // ------------------------------------------------------------------ listening

    void StartListening(string via)
    {
        lock (_gate)
        {
            if (_state is State.Thinking or State.Speaking) InterruptLocked();
            _state = State.Listening;
            _audio.Clear();
            var v = _settings.Current.Voice;
            _vad = new VoiceActivityDetector(v.VadSensitivity, v.EndSilenceMs);
            _listenVia = via;
            _listenClock.Restart();
        }
        _puck.Send(new JsonObject { ["t"] = "ai", ["s"] = "listening" });
    }

    void StopListening(string why)
    {
        short[] clip;
        lock (_gate)
        {
            if (_state != State.Listening) return;
            _state = State.Thinking;
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
        _log.LogInformation("{Puck}: heard {Seconds:0.0} s ({Why})", _puck.Name, clip.Length / (double)Rate, why);
        var cts = _turn;
        _ = Task.Run(() => RunTurnAsync(clip, cts.Token));
    }

    void Cancel()
    {
        lock (_gate)
        {
            InterruptLocked();
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

    async Task RunTurnAsync(short[] clip, CancellationToken cancel)
    {
        try
        {
            if (clip.Length < Rate / 4)
            {
                await NothingHeardAsync(cancel).ConfigureAwait(false);
                return;
            }

            string transcript;
            var sw = Stopwatch.StartNew();
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
            _log.LogInformation("{Puck} heard \"{Text}\" ({Ms} ms)", _puck.Name, transcript, sw.ElapsedMilliseconds);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                await NothingHeardAsync(cancel).ConfigureAwait(false);
                return;
            }
            _puck.Send(new JsonObject { ["t"] = "heard", ["text"] = Clip(transcript, 300) });

            var lower = transcript.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
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

            await AnswerAsync(transcript, cancel).ConfigureAwait(false);
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

    async Task AnswerAsync(string transcript, CancellationToken cancel)
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
        bool followUp = s.Assistant.FollowUpListening && EndsWithQuestion(result.Text);
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

    async Task NothingHeardAsync(CancellationToken cancel)
    {
        _puck.Send(new JsonObject { ["t"] = "heard", ["text"] = "" });
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
    async Task SpeakLoopAsync(ChannelReader<string> sentences, CancellationToken cancel)
    {
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
