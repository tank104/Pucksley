using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pucksley.Sdk;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Pucksley.Plugins.Speech;

public sealed class LocalWhisperSettings
{
    [Setting("Model", Order = 1, Options = ["tiny.en", "base.en", "small.en", "medium.en", "large-v3-turbo"],
        Help = "Bigger is more accurate but slower: tiny.en 75 MB, base.en 142 MB, small.en 466 MB, medium.en 1.5 GB, large-v3-turbo 1.6 GB (multilingual). Downloads on first use.")]
    public string Model { get; set; } = LocalWhisperPlugin.DefaultModel;

    [Setting("Language", Order = 2, Placeholder = "en",
        Help = "A language code such as en or mi, or auto to detect it. Only large-v3-turbo is multilingual; the .en models always use English. Empty follows the hub's language.")]
    public string Language { get; set; } = "en";

    [Setting("Threads", Order = 3, Advanced = true, Help = "CPU threads to use; 0 picks automatically.")]
    public int Threads { get; set; }
}

/// <summary>
/// Offline transcription with whisper.cpp (Whisper.net). The model file downloads into the plugin's data folder on
/// first use (or from the Download model button) and is loaded once; each utterance gets its own processor.
/// </summary>
public sealed partial class LocalWhisperPlugin : SpeechPlugin<LocalWhisperSettings>, ISpeechToText
{
    public const string DefaultModel = "base.en";
    internal const string DownloadBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    const string DownloadAction = "download-model";

    // Older whisper.cpp builds ignore input under a second, so short answers ("yes", "stop") get trailing silence.
    // It costs nothing: whisper always encodes a 30 s window.
    const int MinSamples = 16000 * 5 / 4;

    /// <summary>Loads the native library once per process: the error if it can't, and the languages it knows.</summary>
    static readonly Lazy<(string? Error, HashSet<string> Languages)> Runtime = new(ProbeRuntime);

    readonly object _gate = new();
    CancellationTokenSource _life = new();
    Task<LoadedModel>? _loading;
    Task<string>? _download;
    string? _downloadModel;
    volatile string? _downloadError;
    volatile string? _loadError;
    long _downloadFailedAt;
    int _lastProgress = -1;

    public override string Id => "whisper-local";
    public override string Name => "Whisper (on this computer)";

    public override string Description =>
        "Private, offline speech recognition on this computer with OpenAI's Whisper model (whisper.cpp). The model downloads on first use.";

    public string EngineId => "whisper-local";
    public string EngineName => $"Whisper {Settings.Model} (on this computer)";

    /// <summary>
    /// The native runtime loads and the model is here or can be downloaded. After a failed download it's not
    /// ready for a minute (so the hub doesn't wait on a doomed download every utterance), then it tries again.
    /// </summary>
    public bool IsReady
    {
        get
        {
            var model = Settings.Model;
            if (Runtime.Value.Error is not null || !IsValidModelName(model) || _loadError is not null) return false;
            if (File.Exists(ModelPath(model))) return true;
            return _downloadError is null || Environment.TickCount64 - Interlocked.Read(ref _downloadFailedAt) > 60_000;
        }
    }

    public async Task<string> TranscribeAsync(AudioClip audio, string? language, CancellationToken cancel)
    {
        var s = Settings;
        if (Runtime.Value.Error is { } error) throw new PluginException($"Whisper can't run on this computer: {error}");
        if (AudioConvert.TooShortToTranscribe(audio)) return "";

        LoadedModel model;
        try
        {
            model = await GetModelAsync().WaitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            // The plugin stopped (settings saved, shutdown) while the model was loading: not the caller's cancel.
            throw new PluginException("Whisper is restarting. Try again in a moment.");
        }
        if (!model.TryAcquire()) throw new PluginException("Whisper is restarting. Try again in a moment.");
        try
        {
            var samples = AudioConvert.ToFloats(AudioConvert.Resample(audio.Samples, audio.SampleRate, 16000), MinSamples);
            var builder = model.Factory.CreateBuilder()
                .WithThreads(ThreadCount(s.Threads))
                .WithNoContext()
                .WithSingleSegment();
            var lang = ResolveLanguage(s.Model, s.Language, language, Runtime.Value.Languages);
            builder = lang is null ? builder.WithLanguageDetection() : builder.WithLanguage(lang);

            // DisposeAsync waits for the native work to stop (it keeps going briefly after a cancel), and only then
            // is the model released, so a stop or model change can never free it mid-transcription.
            await using var processor = builder.Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, cancel).ConfigureAwait(false))
                text.Append(segment.Text);
            return CleanTranscript(text.ToString());
        }
        catch (Exception e) when (e is WhisperProcessingException or WhisperModelLoadException)
        {
            throw new PluginException($"Whisper couldn't transcribe that ({e.Message}).", e);
        }
        finally
        {
            model.Release();
        }
    }

    public override async Task StartAsync(CancellationToken cancel)
    {
        lock (_gate) _life = new CancellationTokenSource();
        _downloadError = null;
        _loadError = null;
        await Task.Run(() => Runtime.Value, cancel).ConfigureAwait(false);
        ShowSetup();
        // A model that's already here is probably in use: load it now rather than on the first utterance.
        if (Runtime.Value.Error is null && IsValidModelName(Settings.Model) && File.Exists(ModelPath(Settings.Model)))
            _ = WarmUpAsync();
    }

    public override async Task StopAsync()
    {
        Task<LoadedModel>? loading;
        Task<string>? download;
        lock (_gate)
        {
            _life.Cancel();
            (loading, _loading) = (_loading, null);
            (download, _download) = (_download, null);
        }
        // Drops the plugin's reference; the factory is freed once in-flight transcriptions finish.
        try
        {
            if (loading is not null) (await loading.ConfigureAwait(false)).Dispose();
        }
        catch (Exception)
        {
            // The load failed or was cancelled: nothing to free.
        }
        try
        {
            if (download is not null) await download.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled with everything else; the partial file is already deleted.
        }
    }

    public override IReadOnlyList<PluginAction> GetActions() =>
        [new(DownloadAction, "Download model", "Download (or fetch a fresh copy of) the chosen model now instead of on first use.")];

    public override Task<ActionResult> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> input, CancellationToken cancel)
    {
        if (actionId != DownloadAction) return base.RunActionAsync(actionId, input, cancel);
        var model = Settings.Model;
        if (Runtime.Value.Error is { } error) return Result($"Whisper can't run on this computer: {error}", false);
        if (!IsValidModelName(model)) return Result($"'{model}' isn't a Whisper model name.", false);
        lock (_gate)
        {
            if (_loading is { IsCompletedSuccessfully: true }) return Result($"{model} is downloaded and loaded.");
            if (_loading is { IsCompleted: false }) return Result($"Already getting {model} ready; the status shows progress.");
            if (_loadError is not null)
            {
                // The file is there but won't load: fetch a fresh copy.
                TryDelete(ModelPath(model));
                _loadError = null;
            }
            _downloadError = null;
        }
        bool present = File.Exists(ModelPath(model));
        _ = WarmUpAsync();  // downloads if needed, then loads
        var size = SizeOf(model) is { } mb ? $" ({mb})" : "";
        return Result(present ? $"{model} is already downloaded; loading it." : $"Downloading {model}{size}. The status shows progress.");

        static Task<ActionResult> Result(string message, bool ok = true) => Task.FromResult(new ActionResult(message, ok));
    }

    protected override PluginStatus DescribeSetup()
    {
        var model = Settings.Model;
        if (Runtime.Value.Error is { } error) return new(StatusLevel.Error, $"Whisper can't run on this computer: {error}");
        if (!IsValidModelName(model)) return new(StatusLevel.Error, $"'{model}' isn't a Whisper model name");
        if (_loadError is { } loadError) return new(StatusLevel.Error, loadError);
        if (_downloadError is { } downloadError) return new(StatusLevel.Error, downloadError);
        if (File.Exists(ModelPath(model)))
        {
            return new(StatusLevel.Ok, RuntimeOptions.LoadedLibrary switch
            {
                null => $"Ready: {model}",
                RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => $"Ready: {model} (CPU)",
                var gpu => $"Ready: {model} ({gpu})",
            });
        }
        var size = SizeOf(model) is { } mb ? $" ({mb})" : "";
        return new(StatusLevel.Idle, $"The {model} model{size} downloads on first use, or click Download model");
    }

    /// <summary>The configured model, downloading and loading it if needed. One shared task, retried after a failure.</summary>
    Task<LoadedModel> GetModelAsync()
    {
        lock (_gate)
        {
            if (_loading is null || _loading.IsFaulted || _loading.IsCanceled)
                _loading = LoadModelAsync(Settings.Model, _life.Token);
            return _loading;
        }
    }

    async Task<LoadedModel> LoadModelAsync(string model, CancellationToken life)
    {
        await Task.Yield();  // don't run inside the caller's lock
        if (!IsValidModelName(model)) throw new PluginException($"'{model}' isn't a Whisper model name.");
        var path = await EnsureModelFileAsync(model).WaitAsync(life).ConfigureAwait(false);
        LoadedModel loaded;
        try
        {
            loaded = await Task.Run(() => LoadedModel.Load(model, path), life).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _loadError = $"Couldn't load the {model} model ({e.GetBaseException().Message}). Click Download model to fetch it again.";
            SetStatus(StatusLevel.Error, _loadError);
            Log.LogWarning(e, "Couldn't load Whisper model {Model}", model);
            throw new PluginException(_loadError, e);
        }
        if (life.IsCancellationRequested)
        {
            loaded.Dispose();  // stopped while loading
            throw new OperationCanceledException(life);
        }
        _downloadError = null;  // e.g. the file was copied in by hand after a failed download
        _loadError = null;
        ShowSetup();
        return loaded;
    }

    /// <summary>The model file's path, downloading it first if needed. Concurrent callers share one download.</summary>
    internal Task<string> EnsureModelFileAsync(string model)
    {
        var path = ModelPath(model);
        if (File.Exists(path)) return Task.FromResult(path);
        lock (_gate)
        {
            if (_download is { IsCompleted: false } && _downloadModel == model) return _download;
            _downloadModel = model;
            return _download = DownloadModelAsync(model, path, _life.Token);
        }
    }

    async Task<string> DownloadModelAsync(string model, string path, CancellationToken life)
    {
        await Task.Yield();  // don't run inside the caller's lock
        var url = ModelUrl(model);
        var temp = path + ".download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _downloadError = null;
            Interlocked.Exchange(ref _lastProgress, -1);
            SetStatus(StatusLevel.Warning, $"Downloading {model}…");
            Log.LogInformation("Downloading Whisper model {Model} from {Url}", model, url);
            await ModelDownloader.DownloadAsync(Context.Http, url, temp, (done, total) => ShowProgress(model, done, total), life).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);  // only a complete, checked file ever gets the real name
            Log.LogInformation("Downloaded Whisper model {Model}", model);
            return path;
        }
        catch (Exception e)
        {
            TryDelete(temp);
            if (e is OperationCanceledException && life.IsCancellationRequested) throw;
            var reason = e switch
            {
                OperationCanceledException => "it timed out",
                HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "no internet connection?",
                _ => e.Message,
            };
            _downloadError = $"Couldn't download the {model} model ({reason}). Click Download model to try again.";
            Interlocked.Exchange(ref _downloadFailedAt, Environment.TickCount64);
            SetStatus(StatusLevel.Error, _downloadError);
            Log.LogWarning(e, "Whisper model download failed");
            throw new PluginException(_downloadError, e);
        }
    }

    void ShowProgress(string model, long done, long? total)
    {
        // Only on whole-percent (or whole-MB) changes, so the web page isn't flooded.
        if (total is > 0)
        {
            int percent = (int)(done * 100 / total.Value);
            if (Interlocked.Exchange(ref _lastProgress, percent) != percent) SetStatus(StatusLevel.Warning, $"Downloading {model}… {percent}%");
        }
        else
        {
            int mb = (int)(done >> 20);
            if (Interlocked.Exchange(ref _lastProgress, mb) != mb) SetStatus(StatusLevel.Warning, $"Downloading {model}… {mb} MB");
        }
    }

    async Task WarmUpAsync()
    {
        try
        {
            await GetModelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            // Already in the status; the next utterance (or the button) tries again.
            Log.LogDebug(e, "Whisper warm-up failed");
        }
    }

    internal string ModelPath(string model) => Path.Combine(Context.DataDirectory, "models", $"ggml-{model}.bin");

    internal static Uri ModelUrl(string model) => new($"{DownloadBase}ggml-{model}.bin");

    /// <summary>Safe as a file name and URL segment: lower-case letters, digits, '.', '-', '_'.</summary>
    internal static bool IsValidModelName(string? model) =>
        model is not null && ModelName().IsMatch(model) && !model.Contains("..", StringComparison.Ordinal);

    internal static string? SizeOf(string model) => model switch
    {
        "tiny.en" => "75 MB",
        "base.en" => "142 MB",
        "small.en" => "466 MB",
        "medium.en" => "1.5 GB",
        "large-v3-turbo" => "1.6 GB",
        _ => null,
    };

    /// <summary>
    /// The language for Whisper, or null to detect it: always "en" for the English-only models; otherwise the
    /// setting ("auto" = detect), else the hub's language; unknown codes fall back to detection.
    /// </summary>
    internal static string? ResolveLanguage(string model, string? setting, string? hubLanguage, IReadOnlySet<string> known)
    {
        if (model.Contains(".en", StringComparison.OrdinalIgnoreCase)) return "en";
        var own = setting?.Trim();
        if (string.Equals(own, "auto", StringComparison.OrdinalIgnoreCase)) return null;
        var code = Languages.Primary(string.IsNullOrEmpty(own) ? hubLanguage : own);
        return code is not null && (known.Count == 0 || known.Contains(code)) ? code : null;
    }

    /// <summary>The setting, or about one thread per physical core (at least min(4, cores), at most 8: whisper.cpp gains little beyond).</summary>
    internal static int ThreadCount(int setting) =>
        setting > 0 ? setting : Math.Clamp(Environment.ProcessorCount / 2, Math.Min(4, Environment.ProcessorCount), 8);

    /// <summary>Joined segment text without whisper's non-speech tags ("[BLANK_AUDIO]", "(wind blowing)").</summary>
    internal static string CleanTranscript(string raw)
    {
        var text = Spaces().Replace(BracketTag().Replace(raw, " "), " ").Trim();
        return OnlySoundTags().IsMatch(text) ? "" : text;
    }

    static (string? Error, HashSet<string> Languages) ProbeRuntime()
    {
        try
        {
            _ = WhisperFactory.GetRuntimeInfo();
            return (null, new HashSet<string>(WhisperFactory.GetSupportedLanguages(), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception e)
        {
            return (e.GetBaseException().Message, []);
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ModelName();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketTag();

    [GeneratedRegex(@"^(?:\([^)]*\)|\*[^*]*\*|\s)*$")]
    private static partial Regex OnlySoundTags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// A loaded model, reference-counted: the plugin holds one reference and each transcription another, so the
    /// native context is freed only when the plugin has let go and the last transcription has finished.
    /// </summary>
    internal sealed class LoadedModel : IDisposable
    {
        readonly object _lock = new();
        int _references = 1;
        bool _disposed;

        LoadedModel(string name, WhisperFactory factory)
        {
            Name = name;
            Factory = factory;
        }

        public string Name { get; }
        public WhisperFactory Factory { get; }

        public static LoadedModel Load(string name, string path)
        {
            var factory = WhisperFactory.FromPath(path);
            try
            {
                // FromPath doesn't report a bad file; creating a builder does.
                _ = factory.CreateBuilder();
            }
            catch
            {
                factory.Dispose();
                throw;
            }
            return new LoadedModel(name, factory);
        }

        public bool TryAcquire()
        {
            lock (_lock)
            {
                if (_disposed) return false;
                _references++;
                return true;
            }
        }

        public void Release()
        {
            bool free;
            lock (_lock) free = --_references == 0;
            if (free) Factory.Dispose();
        }

        /// <summary>Drops the owner's reference (once).</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Release();
        }
    }
}
