using Microsoft.Extensions.Logging;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Orca;

public sealed class OrcaSettings
{
    [Setting("Tell me when a session needs me", Order = 1, Help = "A banner on the puck when an agent stops on a question or a permission.")]
    public bool NotifyWaiting { get; set; } = true;

    [Setting("Tell me when a session finishes", Order = 2, Help = "A banner on the puck when a running agent finishes its turn.")]
    public bool NotifyDone { get; set; } = true;

    [Setting("Chime", Order = 3, Help = "Play a sound with the banner.")]
    public bool Chime { get; set; } = true;

    [Setting("Say it out loud", Order = 4, Help = "Speak the news on the puck as well as showing it.")]
    public bool Speak { get; set; }

    [Setting("Check every (seconds)", Order = 5, Advanced = true, Help = "How often to ask Orca (each check takes about half a second).")]
    public int PollSeconds { get; set; } = 4;

    [Setting("Orca command-line tool", Order = 6, Advanced = true, Placeholder = "found automatically",
        Help = "The path to orca.exe, if it isn't on PATH or in Orca's usual install folder.")]
    public string OrcaPath { get; set; } = "";
}

/// <summary>
/// Coding-agent sessions in Orca (Claude Code, Codex…): asks Orca's command-line tool every few
/// seconds, answers "what's waiting on me?", keeps a status tile on the puck, and puts up a banner
/// when a session starts waiting on the person or finishes.
/// </summary>
public sealed class OrcaPlugin : PuckPlugin<OrcaSettings>
{
    const string StatusControl = "status";
    static readonly string[] PsArgs = ["worktree", "ps", "--json"];
    static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(15);

    readonly OrcaTracker _tracker = new();
    readonly object _gate = new();
    CancellationTokenSource? _loop;
    IReadOnlyList<OrcaSession> _sessions = [];
    string _glance = "…";

    public override string Id => "orca";
    public override string Name => "Orca";
    public override string Description =>
        "Your coding-agent sessions in Orca (Claude Code, Codex…): ask what's waiting on you, running or done, and get a heads-up on the puck when one needs you or finishes.";
    public override string Icon => "pc";
    public override bool EnabledByDefault => OrcaCli.Find(null) is not null;

    public override string? Instructions =>
        "Orca is the app where the user runs coding agents (Claude Code, Codex) in workspaces named after their projects. " +
        "For questions about their sessions or agents (\"anything waiting on me?\", \"is the Dimpkin one done?\", \"what's Claude doing?\") use orca_sessions. " +
        "Say workspace names naturally and sum up what a waiting session is asking in a sentence, not its whole message.";

    public override Task StartAsync(CancellationToken cancel)
    {
        _loop?.Cancel();
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        SetStatus(StatusLevel.Idle, "Looking for Orca…");
        _ = Task.Run(() => WatchAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public override Task StopAsync()
    {
        _loop?.Cancel();
        _loop = null;
        return Task.CompletedTask;
    }

    async Task WatchAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var wait = TimeSpan.FromSeconds(Math.Clamp(Settings.PollSeconds, 2, 60));
            try
            {
                var sessions = await LoadAsync(cancel);
                var changes = _tracker.Update(sessions, DateTimeOffset.UtcNow);
                lock (_gate) _sessions = sessions;
                SetGlance(OrcaSessions.Glance(sessions));
                SetStatus(StatusLevel.Ok, StatusLine(sessions));
                foreach (var change in changes) await TellAsync(change, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
            catch (Exception e) when (e is InvalidOperationException or FormatException or System.ComponentModel.Win32Exception or IOException)
            {
                SetStatus(StatusLevel.Warning, e.Message);
                SetGlance("Orca off");
                wait = TimeSpan.FromSeconds(15); // not running (or not installed): ask less often
            }
            try { await Task.Delay(wait, cancel); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task<IReadOnlyList<OrcaSession>> LoadAsync(CancellationToken cancel)
    {
        var exe = OrcaCli.Find(Settings.OrcaPath)
                  ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(Settings.OrcaPath)
                      ? "Orca's command-line tool isn't on this computer (Orca runs on the PC; run this hub there, or share it over MCP)."
                      : $"There's no orca tool at {Settings.OrcaPath.Trim()}.");
        return OrcaSessions.Parse(await OrcaCli.RunJsonAsync(exe, PsArgs, CliTimeout, cancel));
    }

    static string StatusLine(IReadOnlyList<OrcaSession> sessions)
    {
        int waiting = sessions.Count(s => s.State == OrcaState.Waiting), working = sessions.Count(s => s.State == OrcaState.Working);
        int done = sessions.Count(s => s.State == OrcaState.Done);
        return $"Watching {sessions.Count} session{(sessions.Count == 1 ? "" : "s")}: {waiting} waiting on you, {working} running, {done} done";
    }

    async Task TellAsync(OrcaChange change, CancellationToken cancel)
    {
        var s = change.Session;
        string text;
        NotifyKind kind;
        if (s.State == OrcaState.Waiting)
        {
            if (!Settings.NotifyWaiting) return;
            text = $"{s.Workspace} needs you";
            kind = NotifyKind.Alert;
        }
        else
        {
            if (!Settings.NotifyDone) return;
            text = $"{s.Workspace} is done";
            kind = NotifyKind.Info;
        }
        Log.LogInformation("Orca: {Text} ({Title})", text, s.Title);
        try
        {
            await Context.NotifyAsync(text, kind, Settings.Chime);
            if (Settings.Speak) await Context.SayAsync(text, cancel: cancel);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogWarning("Orca: couldn't tell the puck: {Error}", e.Message);
        }
    }

    void SetGlance(string glance)
    {
        lock (_gate)
        {
            if (glance == _glance) return;
            _glance = glance;
        }
        Context.ControlValueChanged(StatusControl, text: glance);
    }

    // ---------------------------------------------------------------- the assistant

    [Tool("orca_sessions",
        "The user's coding-agent sessions in Orca (Claude Code, Codex…), by workspace: which are waiting on them (a question, permission or sign-in), in progress, done or idle, how long, and the agent's last message.")]
    public async Task<ToolResult> Sessions(
        [Param("Only one group (default all)", Values = ["all", "waiting", "working", "done", "idle"])] string which = "all",
        CancellationToken cancel = default)
    {
        IReadOnlyList<OrcaSession> sessions;
        try { sessions = await LoadAsync(cancel); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or System.ComponentModel.Win32Exception or IOException)
        {
            return ToolResult.Error($"I couldn't ask Orca: {e.Message}");
        }
        OrcaState? only = which?.Trim().ToLowerInvariant() switch
        {
            "waiting" or "blocked" => OrcaState.Waiting,
            "working" or "running" or "in progress" => OrcaState.Working,
            "done" or "finished" => OrcaState.Done,
            "idle" => OrcaState.Idle,
            _ => null,
        };
        var waiting = sessions.Count(s => s.State == OrcaState.Waiting);
        return ToolResult.Ok(OrcaSessions.Describe(sessions, DateTimeOffset.UtcNow, only),
            waiting > 0 ? $"{waiting} waiting on you" : $"{sessions.Count(s => s.State == OrcaState.Working)} running");
    }

    // ---------------------------------------------------------------- the puck's tile

    public override IReadOnlyList<PuckControl> GetControls()
    {
        string glance;
        lock (_gate) glance = _glance;
        return [new PuckControl { Id = StatusControl, Name = "Agents", Group = "Orca", Kind = ControlKind.Action, Text = glance }];
    }

    /// <summary>Pressing the tile reads out what's waiting and running.</summary>
    public override async Task OnControlAsync(string controlId, ControlInput input, CancellationToken cancel)
    {
        if (controlId != StatusControl || !input.Pressed) return;
        IReadOnlyList<OrcaSession> sessions;
        lock (_gate) sessions = _sessions;
        await Context.SayAsync(OrcaSessions.Spoken(sessions), cancel: cancel);
    }
}
