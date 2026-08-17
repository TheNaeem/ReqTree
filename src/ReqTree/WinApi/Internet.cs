using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReqTree.WinApi;

/// <summary>
/// wininet.dll. Raw declarations only — the meaning of a call belongs to whoever makes it.
/// </summary>
/// <remarks>
/// One class per Windows library, named after it, holding the imports and the constants they take.
/// Keeping them together means the marshalling for a given API is written once and read in one
/// place, instead of a DllImport appearing in whichever file happened to need it.
/// </remarks>
internal static class Internet
{
    /// <summary>Tells running applications to re-read the proxy settings from the registry.</summary>
    internal const int OptionSettingsChanged = 39;

    /// <summary>Makes them apply what they just re-read.</summary>
    internal const int OptionRefresh = 37;

    /// <remarks>
    /// The search path is pinned to System32. Without it the loader will also look in the
    /// application's own directory, so a wininet.dll dropped next to reqtree.exe would be loaded
    /// instead of the real one — and this process holds the user's privileges and edits their proxy
    /// settings. wininet is a system library and there is nowhere else it should ever come from.
    /// </remarks>
    [DllImport("wininet.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InternetSetOption(
        IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
