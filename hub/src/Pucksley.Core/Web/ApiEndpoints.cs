using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pucksley.Core.Agent;
using Pucksley.Core.Hosting;
using Pucksley.Core.Plugins;
using Pucksley.Core.Pucks;
using Pucksley.Core.Settings;
using Pucksley.Core.Voice;
using Pucksley.Sdk;

namespace Pucksley.Core.Web;

/// <summary>The hub's REST API for its web page, the puck WebSocket, clips and plugin endpoints.</summary>
public static partial class ApiEndpoints
{
    // The page's own files (no paths: nothing outside wwwroot can be asked for)
    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9_.-]+\.(js|css|svg|png|ico|html|json|webmanifest)$")]
    private static partial System.Text.RegularExpressions.Regex PageFile();

    static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// The page is open on this machine; from elsewhere it needs the admin key (header, cookie or
    /// <c>?key=</c>). Changes also need the <c>X-Puck-Ui</c> header, which other sites' pages can't send.
    /// </summary>
    public static void UseAdminAuth(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            bool guarded = path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/plugins/", StringComparison.Ordinal);
            if (!guarded)
            {
                await next().ConfigureAwait(false);
                return;
            }
            var settings = ctx.RequestServices.GetRequiredService<SettingsStore>();
            var token = settings.Current.AdminToken;
            bool loopback = ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
            string? presented = ctx.Request.Headers["X-Puck-Key"].FirstOrDefault()
                                ?? ctx.Request.Cookies["puck_key"]
                                ?? ctx.Request.Query["key"].FirstOrDefault();
            bool keyOk = presented is not null && presented == token;
            if (!loopback && !keyOk)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "This page needs the hub's admin key. Open the link the hub printed when it started." }).ConfigureAwait(false);
                return;
            }
            bool mutating = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method);
            if (mutating && path.StartsWith("/api/", StringComparison.Ordinal) && ctx.Request.Headers["X-Puck-Ui"].FirstOrDefault() != "1")
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-Puck-Ui header." }).ConfigureAwait(false);
                return;
            }
            await next().ConfigureAwait(false);
        });
    }

    public static void MapHub(this WebApplication app)
    {
        // ---- the page (embedded files; ?key= is remembered in a cookie)
        var files = new ManifestEmbeddedOrPlain(typeof(ApiEndpoints).Assembly);
        app.MapGet("/", (HttpContext ctx) => ServePage(ctx, files, "index.html"));
        app.MapGet("/{file}", (HttpContext ctx, string file) =>
            PageFile().IsMatch(file) ? ServePage(ctx, files, file) : Results.NotFound());

        // ---- pucks
        app.Map("/puck", async (HttpContext ctx, PuckManager pucks, SettingsStore settings) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            if (!settings.Current.Sharing.AcceptPucks)
            {
                // this hub lends its tools to the main hub: the puck should look for that one
                ctx.Response.StatusCode = 403;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var remote = $"{ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}";
            var connection = new WebSocketPuckConnection(socket, remote);
            pucks.Attach(connection);
            await connection.RunAsync(ctx.RequestAborted).ConfigureAwait(false);
        });

        // ---- clips for LAN speakers (unguessable ids, no key)
        app.MapGet("/clips/{id}", (string id, ClipStore clips) =>
        {
            var clean = Path.GetFileNameWithoutExtension(id);
            return clips.Get(clean) is { } c ? Results.Bytes(c.Data, c.Type) : Results.NotFound();
        });

        // ---- plugin endpoints (OAuth callbacks…)
        app.Map("/plugins/{id}/{**path}", async (HttpContext ctx, string id, string? path, PluginManager plugins) =>
        {
            var h = plugins.Find(id);
            if (h is null || !h.Enabled) return Results.NotFound();
            string? body = null;
            if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
            {
                using var reader = new StreamReader(ctx.Request.Body);
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            var req = new PluginHttpRequest(ctx.Request.Method, path ?? "",
                ctx.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()),
                ctx.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString()), body);
            var res = await h.Plugin.HandleHttpAsync(req, ctx.RequestAborted).ConfigureAwait(false);
            if (res is null) return Results.NotFound();
            return res.Data is { } data ? Results.Bytes(data, res.ContentType) : Results.Content(res.Body, res.ContentType, Encoding.UTF8, res.Status);
        });

        var api = app.MapGroup("/api");

        api.MapGet("/status", (HttpContext ctx, SettingsStore settings, PluginManager plugins, PuckManager pucks, SpeechService speech, AgentService agent, HubRuntime runtime) =>
        {
            var s = settings.Current;
            bool loopback = ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
            var connected = pucks.Sessions.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var stt = speech.Stt;
            var tts = speech.Tts;
            return Results.Json(new JsonObject
            {
                ["hub"] = new JsonObject
                {
                    ["name"] = s.HubName,
                    ["id"] = s.HubId,
                    ["version"] = typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(3),
                    ["port"] = HubHost.ActualPort > 0 ? HubHost.ActualPort : s.Port,
                    ["lanUrl"] = runtime.LanUrl.ToString(),
                    ["adminUrl"] = loopback ? $"{runtime.LanUrl}?key={s.AdminToken}" : null,
                    ["dataDir"] = loopback ? HubPaths.DataDir : null,
                    ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    ["windows"] = OperatingSystem.IsWindows(),
                    ["startedAt"] = StartedAt.ToString("O"),
                },
                ["assistant"] = new JsonObject
                {
                    ["ready"] = !string.IsNullOrEmpty(agent.ApiKey),
                    ["model"] = s.Assistant.Model,
                    ["toolCount"] = plugins.Tools.Count,
                },
                ["voice"] = new JsonObject
                {
                    ["stt"] = stt is null ? null : Engine(stt.EngineId, stt.EngineName, stt.IsReady),
                    ["tts"] = tts is null ? null : Engine(tts.EngineId, tts.EngineName, tts.IsReady),
                    ["sttEngines"] = new JsonArray(plugins.SttEngines.Select(e => (JsonNode)Engine(e.EngineId, e.EngineName, e.IsReady)).ToArray()),
                    ["ttsEngines"] = new JsonArray(plugins.TtsEngines.Select(e => (JsonNode)Engine(e.EngineId, e.EngineName, e.IsReady)).ToArray()),
                },
                ["pucks"] = new JsonArray(s.Pucks.Select(r => (JsonNode)PuckJson(r, connected.GetValueOrDefault(r.Id), pucks)).ToArray()),
                ["pending"] = new JsonArray(pucks.Pending.Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Id, ["name"] = p.Name, ["code"] = p.Code, ["address"] = p.Address, ["since"] = p.Since.ToString("O"),
                }).ToArray()),
                ["plugins"] = new JsonArray(plugins.Plugins.OrderBy(p => p.Plugin.Name).Select(p => (JsonNode)PluginSummary(p, plugins)).ToArray()),
                ["mediaSources"] = new JsonArray(plugins.MediaSources.Select(m => (JsonNode)new JsonObject { ["id"] = m.GlobalId, ["name"] = m.Name }).ToArray()),
            });
        });

        // ---- hub-wide settings forms
        api.MapGet("/settings/{section}", (string section, SettingsStore settings) =>
        {
            var (type, value) = Section(settings.Current, section);
            if (type is null) return Results.NotFound();
            return Results.Json(new JsonObject { ["schema"] = SettingsSchema.Describe(type), ["values"] = SettingsSchema.ToUi(value!) });
        });

        api.MapPut("/settings/{section}", async (string section, HttpContext ctx, SettingsStore settings) =>
        {
            var body = await ReadJson(ctx).ConfigureAwait(false);
            var (type, current) = Section(settings.Current, section);
            if (type is null || body is null) return Results.BadRequest();
            var updated = SettingsSchema.FromUi(body, type, current!);
            settings.Update(s =>
            {
                switch (updated)
                {
                    case AssistantSettings a: s.Assistant = a; break;
                    case VoiceSettings v: s.Voice = v; break;
                    case HouseholdSettings h: s.Household = h; break;
                    case DirectSettings d: s.Direct = d; break;
                    case SharingSettings sh: sh.ShareKey = s.Sharing.ShareKey; s.Sharing = sh; break;
                    case HubIdentity hi: s.HubName = string.IsNullOrWhiteSpace(hi.Name) ? Environment.MachineName : hi.Name.Trim(); break;
                }
            });
            var (_, after) = Section(settings.Current, section);
            return Results.Json(new JsonObject { ["schema"] = SettingsSchema.Describe(type), ["values"] = SettingsSchema.ToUi(after!) });
        });

        // ---- sharing this hub's tools with another hub (MCP)
        api.MapGet("/sharing", (SettingsStore settings, HubRuntime runtime) => Results.Json(SharingJson(settings, runtime)));
        api.MapPost("/sharing/new-key", (SettingsStore settings, HubRuntime runtime) =>
        {
            settings.Update(s => s.Sharing.ShareKey = ToolSharing.NewKey());
            return Results.Json(SharingJson(settings, runtime));
        });

        // ---- plugins
        api.MapGet("/plugins/{id}", (string id, PluginManager plugins) =>
        {
            var h = plugins.Find(id);
            return h is null ? Results.NotFound() : Results.Json(PluginDetail(h, plugins));
        });

        api.MapPut("/plugins/{id}/settings", async (string id, HttpContext ctx, PluginManager plugins) =>
        {
            var h = plugins.Find(id);
            var body = await ReadJson(ctx).ConfigureAwait(false);
            if (h is null || body is null) return Results.NotFound();
            object updated;
            try { updated = SettingsSchema.FromUi(body, h.Plugin.SettingsType, plugins.CurrentSettings(h)); }
            catch (JsonException e) { return Results.BadRequest(new { error = e.Message }); }
            await plugins.UpdateSettingsAsync(h.Id, updated).ConfigureAwait(false);
            return Results.Json(PluginDetail(h, plugins));
        });

        api.MapPost("/plugins/{id}/enabled", async (string id, HttpContext ctx, PluginManager plugins) =>
        {
            var h = plugins.Find(id);
            var body = await ReadJson(ctx).ConfigureAwait(false);
            if (h is null) return Results.NotFound();
            await plugins.SetEnabledAsync(h.Id, body?["enabled"]?.GetValue<bool>() ?? true).ConfigureAwait(false);
            return Results.Json(PluginDetail(h, plugins));
        });

        api.MapPost("/plugins/{id}/actions/{actionId}", async (string id, string actionId, HttpContext ctx, PluginManager plugins) =>
        {
            var h = plugins.Find(id);
            if (h is null) return Results.NotFound();
            var body = await ReadJson(ctx).ConfigureAwait(false);
            var inputs = new Dictionary<string, string>();
            if (body?["inputs"] is JsonObject o)
                foreach (var kv in o) inputs[kv.Key] = kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : kv.Value?.ToJsonString() ?? "";
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                var r = await h.Plugin.RunActionAsync(actionId, inputs, cts.Token).ConfigureAwait(false);
                return Results.Json(new { message = r.Message, ok = r.Ok, openUrl = r.OpenUrl });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return Results.Json(new { message = e.Message, ok = false, openUrl = (string?)null });
            }
        });

        // ---- pucks
        api.MapGet("/pucks/{id}", (string id, PuckManager pucks) =>
        {
            var r = pucks.Record(id);
            if (r is null) return Results.NotFound();
            return Results.Json(new JsonObject
            {
                ["info"] = PuckJson(r, pucks.Session(id), pucks),
                ["schema"] = SettingsSchema.Describe(typeof(PuckRecord)),
                ["values"] = SettingsSchema.ToUi(r),
            });
        });

        api.MapPut("/pucks/{id}", async (string id, HttpContext ctx, PuckManager pucks) =>
        {
            var r = pucks.Record(id);
            var body = await ReadJson(ctx).ConfigureAwait(false);
            if (r is null || body is null) return Results.NotFound();
            var updated = (PuckRecord)SettingsSchema.FromUi(body, typeof(PuckRecord), r);
            pucks.UpdateRecord(id, rec =>
            {
                foreach (var p in typeof(PuckRecord).GetProperties().Where(p => p.GetCustomAttribute<SettingAttribute>() is not null && p.CanWrite))
                    p.SetValue(rec, p.GetValue(updated));
            });
            return Results.Ok();
        });

        api.MapPost("/pucks/{id}/approve", async (string id, HttpContext ctx, PuckManager pucks) =>
        {
            var body = await ReadJson(ctx).ConfigureAwait(false);
            return pucks.Approve(id, body?["name"]?.GetValue<string>(), body?["room"]?.GetValue<string>()) ? Results.Ok() : Results.NotFound();
        });

        api.MapPost("/pucks/{id}/reject", (string id, PuckManager pucks) => { pucks.Reject(id); return Results.Ok(); });

        api.MapDelete("/pucks/{id}", (string id, PuckManager pucks) => { pucks.Forget(id); return Results.Ok(); });

        api.MapPost("/pucks/{id}/wifi", async (string id, HttpContext ctx, PuckManager pucks) =>
        {
            var body = await ReadJson(ctx).ConfigureAwait(false);
            var ssid = body?["ssid"]?.GetValue<string>();
            if (string.IsNullOrEmpty(ssid)) return Results.BadRequest(new { error = "ssid is required" });
            var ok = pucks.SendWifi(id, ssid, body?["password"]?.GetValue<string>() ?? "", body?["hubUrl"]?.GetValue<string>());
            return ok ? Results.Ok() : Results.NotFound(new { error = "That puck isn't connected." });
        });

        api.MapPost("/pucks/{id}/say", async (string id, HttpContext ctx, PuckManager pucks) =>
        {
            var body = await ReadJson(ctx).ConfigureAwait(false);
            var s = pucks.Session(id);
            var text = body?["text"]?.GetValue<string>();
            if (s is null || string.IsNullOrWhiteSpace(text)) return Results.NotFound();
            _ = s.Voice.SayAsync(text, CancellationToken.None);
            return Results.Ok();
        });

        api.MapPost("/pucks/{id}/listen", (string id, PuckManager pucks) =>
        {
            var s = pucks.Session(id);
            if (s is null) return Results.NotFound();
            s.Send(new JsonObject { ["t"] = "listen" });
            return Results.Ok();
        });

        // Ask the puck for a snapshot of its board (touch, sensors, sound, links) on every link it has:
        // each answer goes to the hub's log, so a link that doesn't answer shows up too.
        api.MapPost("/pucks/{id}/diag", (string id, PuckManager pucks) =>
        {
            var s = pucks.Session(id);
            if (s is null) return Results.NotFound();
            foreach (var c in s.Connections) c.Send(new JsonObject { ["t"] = "diag" });
            return Results.Ok();
        });

        // A banner on one puck, as plugins send them (to try the screen and chime)
        api.MapPost("/pucks/{id}/notify", async (string id, HttpContext ctx, PuckManager pucks) =>
        {
            var body = await ReadJson(ctx).ConfigureAwait(false);
            var text = body?["text"]?.GetValue<string>();
            if (pucks.Session(id) is null || string.IsNullOrWhiteSpace(text)) return Results.NotFound();
            var kind = (body?["kind"]?.GetValue<string>()) switch { "alert" => NotifyKind.Alert, "ai" => NotifyKind.Ai, _ => NotifyKind.Info };
            var chime = body?["chime"] is System.Text.Json.Nodes.JsonValue c && c.TryGetValue<bool>(out var on) ? on : true;
            await pucks.NotifyAsync(text, kind, chime, id).ConfigureAwait(false);
            return Results.Ok();
        });

        // ---- typing to the assistant
        api.MapPost("/chat", async (HttpContext ctx, AgentService agent, PuckManager pucks) =>
        {
            var body = await ReadJson(ctx).ConfigureAwait(false);
            var text = body?["text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest();
            var session = body?["session"]?.GetValue<string>() ?? "web";
            var activities = new JsonArray();
            var callbacks = new AgentCallbacks
            {
                OnActivity = (icon, t, state) => { lock (activities) activities.Add(new JsonObject { ["icon"] = icon, ["text"] = t, ["state"] = state }); },
            };
            var result = await agent.AskAsync("web:" + session, text, ToolCaller.Web, callbacks, ctx.RequestAborted).ConfigureAwait(false);
            if (body?["speakOn"]?.GetValue<string>() is { } puckId && pucks.Session(puckId) is { } puck && !result.Failed)
                _ = puck.Voice.SayAsync(result.Text, CancellationToken.None);
            return Results.Json(new { text = result.Text, failed = result.Failed, activities });
        });

        api.MapPost("/chat/reset", async (HttpContext ctx, AgentService agent) =>
        {
            var body = await ReadJson(ctx).ConfigureAwait(false);
            agent.Reset("web:" + (body?["session"]?.GetValue<string>() ?? "web"));
            return Results.Ok();
        });

        // ---- voice tests from the browser: speak a line, or the whole pipeline from a recording
        api.MapPost("/voice/speak", async (HttpContext ctx, SpeechService speech) =>
        {
            var body = await ReadJson(ctx).ConfigureAwait(false);
            var text = body?["text"]?.GetValue<string>() ?? "Hello! This is how I sound.";
            try
            {
                var wav = await speech.SynthesizeWavAsync(text, ctx.RequestAborted).ConfigureAwait(false);
                return Results.Bytes(wav, "audio/wav");
            }
            catch (PluginException e) { return Results.BadRequest(new { error = e.Message }); }
        });

        // Speech to text only: body a WAV, ?engine= to pick one (to compare them). Reports how long it took.
        api.MapPost("/voice/transcribe", async (HttpContext ctx, SpeechService speech) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted).ConfigureAwait(false);
            AudioClip clip;
            try { clip = Wav.Decode(ms.ToArray()); }
            catch (FormatException e) { return Results.BadRequest(new { error = e.Message }); }
            var pcm = Resampler.Convert(clip.Samples, clip.SampleRate, SpeechService.Rate);
            var engine = ctx.Request.Query["engine"].FirstOrDefault();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var text = await speech.TranscribeAsync(pcm, string.IsNullOrWhiteSpace(engine) ? null : engine, ctx.RequestAborted).ConfigureAwait(false);
                return Results.Json(new { text, ms = sw.ElapsedMilliseconds, engine });
            }
            catch (PluginException e) { return Results.BadRequest(new { error = e.Message, ms = sw.ElapsedMilliseconds }); }
        });

        api.MapPost("/voice/ask", async (HttpContext ctx, SpeechService speech, AgentService agent) =>
        {
            // Body: a WAV recording (any rate/channels). Runs speech → Claude → speech.
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted).ConfigureAwait(false);
            AudioClip clip;
            try { clip = Wav.Decode(ms.ToArray()); }
            catch (FormatException e) { return Results.BadRequest(new { error = e.Message }); }
            var pcm = Resampler.Convert(clip.Samples, clip.SampleRate, SpeechService.Rate);
            string heard;
            try { heard = await speech.TranscribeAsync(pcm, ctx.RequestAborted).ConfigureAwait(false); }
            catch (PluginException e) { return Results.BadRequest(new { error = e.Message }); }
            if (string.IsNullOrWhiteSpace(heard)) return Results.Json(new { heard = "", reply = "Sorry, I didn't catch that.", failed = true, audio = (string?)null });
            var result = await agent.AskAsync("web:voice", heard, ToolCaller.Web, new AgentCallbacks(), ctx.RequestAborted).ConfigureAwait(false);
            string? audio = null;
            try { audio = Convert.ToBase64String(await speech.SynthesizeWavAsync(result.Text, ctx.RequestAborted).ConfigureAwait(false)); }
            catch (PluginException) { }
            return Results.Json(new { heard, reply = result.Text, failed = result.Failed, audio });
        });

        // ---- live feed
        api.MapGet("/events", async (HttpContext ctx, HubEvents events) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await ctx.Response.WriteAsync(": hello\n\n", ctx.RequestAborted).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            try
            {
                await foreach (var e in events.SubscribeAsync(ctx.RequestAborted).ConfigureAwait(false))
                {
                    await ctx.Response.WriteAsync($"data: {e.ToJsonString()}\n\n", ctx.RequestAborted).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });

        api.MapGet("/logs", (HubEvents events) => Results.Json(events.RecentLogs));
        api.MapGet("/conversation", (HubEvents events) => Results.Json(events.RecentConversation));
        api.MapGet("/usb-ports", () => Results.Json(SerialScanner.FindPuckPorts()));
    }

    // ------------------------------------------------------------------ helpers

    static JsonObject Engine(string id, string name, bool ready) => new() { ["id"] = id, ["name"] = name, ["ready"] = ready };

    /// <summary>What another hub needs to use this one's tools: the MCP address and key, and its own setting lines.</summary>
    static JsonObject SharingJson(SettingsStore settings, HubRuntime runtime)
    {
        var sh = settings.Current.Sharing;
        var key = ToolSharing.EnsureKey(settings);
        var url = runtime.LanUrl.ToString().TrimEnd('/') + ToolSharing.Path;
        var name = new string(settings.Current.HubName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return new JsonObject
        {
            ["shareTools"] = sh.ShareTools,
            ["acceptPucks"] = sh.AcceptPucks,
            ["url"] = url,
            ["key"] = key,
            ["header"] = $"Authorization: Bearer {key}",
            ["serverName"] = name.Length is > 0 and <= 12 ? name : "pc",
        };
    }

    static (Type? Type, object? Value) Section(HubSettings s, string section) => section switch
    {
        "assistant" => (typeof(AssistantSettings), s.Assistant),
        "voice" => (typeof(VoiceSettings), s.Voice),
        "household" => (typeof(HouseholdSettings), s.Household),
        "direct" => (typeof(DirectSettings), s.Direct),
        "sharing" => (typeof(SharingSettings), s.Sharing),
        "hub" => (typeof(HubIdentity), new HubIdentity { Name = s.HubName }),
        _ => (null, null),
    };

    static JsonObject PuckJson(PuckRecord r, PuckSession? s, PuckManager pucks) => new()
    {
        ["id"] = r.Id,
        ["name"] = r.DisplayName,
        ["deviceName"] = r.DeviceName,
        ["room"] = r.Room,
        ["connected"] = s is not null,
        ["transports"] = new JsonArray((s?.Connections ?? []).Select(c => (JsonNode)c.Kind).Distinct().ToArray()),
        ["address"] = s?.Primary?.Description,
        ["ip"] = s?.Ip,
        ["rssi"] = s?.Rssi,
        ["ssid"] = s?.Ssid,
        ["battery"] = s?.Battery,
        ["charging"] = s?.Charging,
        ["usbPower"] = s?.UsbPower,
        ["firmware"] = s?.Firmware,
        ["face"] = s?.Face,
        ["voice"] = s?.Voice.StateName,
        ["directMode"] = s?.DirectMode,
        ["mediaSource"] = s is null ? null : pucks.SourceFor(s)?.Name,
        ["lastSeen"] = (s is not null ? DateTimeOffset.UtcNow : r.LastSeen).ToString("O"),
    };

    static JsonObject PluginSummary(PluginHost h, PluginManager plugins) => new()
    {
        ["id"] = h.Id,
        ["name"] = h.Plugin.Name,
        ["description"] = h.Plugin.Description,
        ["icon"] = h.Plugin.Icon,
        ["enabled"] = h.Enabled,
        ["running"] = h.Running,
        ["status"] = new JsonObject
        {
            ["level"] = h.StartError is not null ? "Error" : h.Enabled ? h.Plugin.Status.Level.ToString() : "Idle",
            ["message"] = h.StartError ?? (h.Enabled ? h.Plugin.Status.Message : "Off"),
        },
        ["toolCount"] = h.Enabled ? plugins.Tools.Count(t => t.PluginId == h.Id) : SafeToolCount(h),
        ["source"] = h.Source,
        ["kinds"] = new JsonArray(Kinds(h.Plugin).Select(k => (JsonNode)k).ToArray()),
    };

    static int SafeToolCount(PluginHost h)
    {
        try { return h.Plugin.GetTools().Count; } catch { return 0; }
    }

    static IEnumerable<string> Kinds(IPlugin p)
    {
        if (p is ISpeechToText) yield return "speech-to-text";
        if (p is ITextToSpeech) yield return "voice";
        if (p is IMediaSourceProvider) yield return "media";
    }

    static JsonObject PluginDetail(PluginHost h, PluginManager plugins)
    {
        var d = PluginSummary(h, plugins);
        d["schema"] = SettingsSchema.Describe(h.Plugin.SettingsType);
        d["values"] = SettingsSchema.ToUi(plugins.CurrentSettings(h));
        IReadOnlyList<PluginAction> actions = [];
        try { actions = h.Plugin.GetActions(); } catch { }
        d["actions"] = new JsonArray(actions.Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a.Id, ["label"] = a.Label, ["description"] = a.Description,
            ["inputs"] = new JsonArray((a.Inputs ?? []).Select(i => (JsonNode)new JsonObject
            {
                ["key"] = i.Key, ["label"] = i.Label, ["placeholder"] = i.Placeholder, ["secret"] = i.Secret,
            }).ToArray()),
        }).ToArray());
        IReadOnlyList<ToolDefinition> tools = [];
        try { tools = h.Plugin.GetTools(); } catch { }
        d["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["confirm"] = t.Confirm }).ToArray());
        return d;
    }

    static async Task<JsonObject?> ReadJson(HttpContext ctx)
    {
        try { return await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false) as JsonObject; }
        catch (JsonException) { return null; }
    }

    static IResult ServePage(HttpContext ctx, ManifestEmbeddedOrPlain files, string name)
    {
        if (ctx.Request.Query["key"].FirstOrDefault() is { Length: > 0 } key)
            ctx.Response.Cookies.Append("puck_key", key, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(365) });
        var bytes = files.Read(name);
        if (bytes is null) return Results.NotFound();
        var type = Path.GetExtension(name) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream",
        };
        ctx.Response.Headers.CacheControl = "no-cache";
        return Results.Bytes(bytes, type);
    }

    /// <summary>
    /// The web page's files: embedded in the assembly, unless a <c>wwwroot</c> folder sits next to
    /// the app (handy while editing the page: no rebuild).
    /// </summary>
    sealed class ManifestEmbeddedOrPlain(Assembly assembly)
    {
        public byte[]? Read(string name)
        {
            var dev = Environment.GetEnvironmentVariable("PUCKSLEY_WWWROOT");
            foreach (var dir in new[] { dev, Path.Combine(AppContext.BaseDirectory, "wwwroot") })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
            var resource = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(".wwwroot." + name, StringComparison.OrdinalIgnoreCase));
            if (resource is null) return null;
            using var s = assembly.GetManifestResourceStream(resource)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}

/// <summary>The hub's own name, as a settings form.</summary>
public sealed class HubIdentity
{
    [Setting("Hub name", Help = "Shown on the pucks and in this page.")]
    public string Name { get; set; } = "";
}
