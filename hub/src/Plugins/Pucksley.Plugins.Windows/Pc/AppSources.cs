using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Pucksley.Plugins.Windows.Common;

namespace Pucksley.Plugins.Windows.Pc;

/// <summary>Where <see cref="AppIndex"/> gets its apps: Start Menu shortcuts and shell:AppsFolder.</summary>
internal static class AppSources
{
    static readonly HashSet<string> ShortcutExtensions = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".appref-ms" };

    public static async Task<IReadOnlyList<AppEntry>> LoadAsync(ILogger log, CancellationToken cancel)
    {
        var apps = new List<AppEntry>(await Task.Run(StartMenuShortcuts, cancel).ConfigureAwait(false));
        try
        {
            apps.AddRange(await Blocking.RunStaAsync(AppsFolder, TimeSpan.FromSeconds(20), "Listing the Start menu's apps", cancel).ConfigureAwait(false));
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            log.LogWarning(e, "Couldn't enumerate shell:AppsFolder; falling back to Get-StartApps");
            try
            {
                apps.AddRange(await StartAppsFromPowerShellAsync(cancel).ConfigureAwait(false));
            }
            catch (Exception e2) when (!cancel.IsCancellationRequested)
            {
                log.LogWarning(e2, "Get-StartApps failed too; only Start Menu shortcuts are known");
            }
        }
        return apps;
    }

    /// <summary>Shortcuts under the all-users and per-user Start Menu "Programs" folders.</summary>
    public static List<AppEntry> StartMenuShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), // %ProgramData%\Microsoft\Windows\Start Menu\Programs
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),       // %AppData%\Microsoft\Windows\Start Menu\Programs
        };
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var apps = new List<AppEntry>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (ShortcutExtensions.Contains(Path.GetExtension(file)))
                    apps.Add(new AppEntry(Path.GetFileNameWithoutExtension(file), file, AppKind.Shortcut));
            }
        }
        return apps;
    }

    // ───────────────────────── shell:AppsFolder through the shell's item API (what Get-StartApps reads)

    const uint SIGDN_NORMALDISPLAY = 0x00000000, SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object BindToHandler(nint bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid handler, [MarshalAs(UnmanagedType.LPStruct)] Guid iid);

        IShellItem GetParent();

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetDisplayName(uint sigdn);

        uint GetAttributes(uint mask);

        int Compare(IShellItem other, uint hint);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);

        void Skip(uint count);

        void Reset();

        IEnumShellItems Clone();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    static extern IShellItem SHCreateItemFromParsingName(string path, nint bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid);

    /// <summary>
    /// Every app Start lists (Store apps included): display name + parsing name, which for Store apps
    /// is the AUMID ("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"). Run on an STA thread.
    /// </summary>
    internal static List<AppEntry> AppsFolder()
    {
        var apps = new List<AppEntry>();
        var folder = SHCreateItemFromParsingName("shell:AppsFolder", 0, typeof(IShellItem).GUID);
        object? enumerator = null;
        try
        {
            enumerator = folder.BindToHandler(0, BHID_EnumItems, typeof(IEnumShellItems).GUID);
            var items = (IEnumShellItems)enumerator;
            while (items.Next(1, out var item, out uint fetched) == 0 && fetched == 1 && item is not null)
            {
                try
                {
                    string name = item.GetDisplayName(SIGDN_NORMALDISPLAY);
                    string id = item.GetDisplayName(SIGDN_PARENTRELATIVEPARSING);
                    apps.Add(new AppEntry(name, id, AppKind.AppsFolder));
                }
                catch (COMException)
                {
                    // an entry without a parsing name: skip it
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            Marshal.ReleaseComObject(folder);
        }
        return apps;
    }

    /// <summary>The same list from PowerShell's Get-StartApps (slower: a second or two).</summary>
    static async Task<List<AppEntry>> StartAppsFromPowerShellAsync(CancellationToken cancel)
    {
        var result = await ProcessRunner.PowerShellAsync("Get-StartApps | ForEach-Object { $_.Name + \"`t\" + $_.AppID }", TimeSpan.FromSeconds(30), cancel).ConfigureAwait(false);
        return result.Output.Split('\n')
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0)
            .Select(parts => new AppEntry(parts[0].Trim(), parts[1].Trim(), AppKind.AppsFolder))
            .ToList();
    }
}
