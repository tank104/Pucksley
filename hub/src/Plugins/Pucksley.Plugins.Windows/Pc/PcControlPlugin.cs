namespace Pucksley.Plugins.Windows.Pc;

/// <summary>
/// The Windows PC the hub runs on. The tools are <see cref="PcControlPluginBase"/> (shared with
/// Linux); <see cref="WindowsDesktop"/> is what does them here.
/// </summary>
public sealed class PcControlPlugin : PcControlPluginBase
{
    public PcControlPlugin() : base(new WindowsDesktop()) { }

    internal PcControlPlugin(AppIndex apps) : base(new WindowsDesktop(), apps) { }
}
