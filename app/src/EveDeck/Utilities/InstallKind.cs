using System.IO;
using Microsoft.Win32;

namespace EveDeck.Utilities;

// Is this process running from the Inno Setup install? Only that copy may update itself: the
// installer owns its folder and registry entries, so re-running a newer installer over it is the
// supported upgrade. A portable copy was unzipped wherever the user liked -- a silent installer
// would put a SECOND copy in %LOCALAPPDATA%\Programs rather than update it -- and the Store build
// is updated by the Store.
internal static class InstallKind
{
    // Must match AppId in app/installer/EveDeck.iss (Inno appends "_is1" to the uninstall key).
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F3E1C2A-7B6D-4E1A-9C3F-2D6A8B4E5F10}_is1";

    private static bool? _isInnoInstall;

    public static bool IsInnoInstall => _isInnoInstall ??= Detect();

    private static bool Detect()
    {
        if (PackagedAppInfo.IsPackaged) return false;
        try
        {
            // PrivilegesRequired=lowest, so the key lives under HKCU. Both halves must agree: the
            // key alone would also match a portable copy on a machine that has the installer too.
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is not string location || location.Length == 0) return false;
            return SameDirectory(location, AppContext.BaseDirectory)
                && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
        }
        catch
        {
            return false;
        }
    }

    internal static bool SameDirectory(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
