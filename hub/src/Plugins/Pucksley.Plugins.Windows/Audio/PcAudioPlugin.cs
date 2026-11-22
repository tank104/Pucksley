using Microsoft.Extensions.Logging;

namespace Pucksley.Plugins.Windows.Audio;

/// <summary>
/// This Windows PC's sound. The tools, the dial and the Home face are <see cref="PcAudioPluginBase"/>;
/// this hands it Core Audio (volume, outputs, per-app sessions) and Windows' media sessions.
/// </summary>
public sealed class PcAudioPlugin : PcAudioPluginBase
{
    public PcAudioPlugin() : this(log => new CoreAudio(log), log => new WindowsMedia(log)) { }

    internal PcAudioPlugin(Func<ILogger, IPcAudio> createAudio, Func<ILogger, IPcMedia> createMedia)
        : base("Windows", "Windows media sessions", createAudio, createMedia) { }
}
