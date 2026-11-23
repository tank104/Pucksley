namespace Pucksley.Plugins.Linux.Pc;

/// <summary>
/// The Linux PC the hub runs on. The tools are <see cref="PcControlPluginBase"/> (shared with
/// Windows); <see cref="LinuxDesktop"/> is what does them here.
/// </summary>
public sealed class PcControlPlugin : PcControlPluginBase
{
    public PcControlPlugin() : base(new LinuxDesktop()) { }

    internal PcControlPlugin(IPcDesktop desktop, AppIndex? apps = null) : base(desktop, apps) { }
}
