using Microsoft.Extensions.Logging;

namespace Pucksley.Plugins.Linux.Audio;

/// <summary>
/// This Linux PC's sound. The tools, the dial and the Home face are <see cref="PcAudioPluginBase"/>
/// (the same as on Windows); this hands it the sound server (PipeWire or PulseAudio, through pactl)
/// and the desktop's media players (MPRIS).
/// </summary>
public sealed class PcAudioPlugin : PcAudioPluginBase
{
    public PcAudioPlugin() : this(log => new PulseAudio(log), log => new MprisMedia(log)) { }

    internal PcAudioPlugin(Func<ILogger, IPcAudio> createAudio, Func<ILogger, IPcMedia> createMedia)
        : base("Linux", "media players", createAudio, createMedia) { }
}
