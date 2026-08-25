using System.ComponentModel;
using System.Text.Json;
using ReqTree.App;
using ReqTree.WinApi;
using Serilog;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace ReqTree.Proxy;

/// <summary>
/// Owns ReqTree's temporary claim on the current user's Windows proxy settings.
/// </summary>
/// <remarks>
/// The process that changes the setting is the only one that knows its original values. This class
/// keeps those values, the crash-recovery marker, and the cross-process semaphore together so a
/// caller cannot restore one without releasing or recording the others.
/// </remarks>
internal sealed class SystemProxyLease : IDisposable
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string OwnershipName = @"Local\ReqTree.SystemProxyOwner";

    private Semaphore? _ownership;
    private (int? Enable, string? Server) _original;

    internal bool IsTaken { get; private set; }

    internal SystemProxyTakeover TakeOver(
        ProxyServer server, ExplicitProxyEndPoint endpoint, int port)
    {
        if (!TryClaimOwnership()) return SystemProxyTakeover.OwnedByAnotherReqTree;

        if (!TryReadSettings(out _original))
            throw new InvalidOperationException(
                "Could not read the current system proxy settings, so ReqTree refused to take them over.");

        server.SetAsSystemProxy(endpoint, ProxyProtocolType.AllHttp);
        IsTaken = true;
        RecordState(_original, port);
        return SystemProxyTakeover.Taken;
    }

    /// <summary>
    /// Restores the exact values captured before takeover. False means nothing was written and the
    /// marker remains for the next process to repair.
    /// </summary>
    internal bool TryRestore()
    {
        if (!RestoreSettings(_original)) return false;

        IsTaken = false;
        ClearState();
        return true;
    }

    /// <summary>
    /// The failed-start path retains its existing best-effort behavior: if the restore operation
    /// itself returns normally, tear down this lease and let the surrounding error report explain
    /// the failed start.
    /// </summary>
    internal void RestoreAfterFailedStart()
    {
        RestoreSettings(_original);
        IsTaken = false;
        ClearState();
    }

    internal void ReleaseOwnership()
    {
        var ownership = Interlocked.Exchange(ref _ownership, null);
        if (ownership is null) return;

        try
        {
            ownership.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already released. There is nothing to undo.
        }
        finally
        {
            ownership.Dispose();
        }
    }

    /// <summary>Undoes a marker left behind by a process that is no longer alive.</summary>
    internal string? CleanStaleState()
    {
        var stale = FindStaleState();
        if (stale is null) return null;

        if (stale.OriginalProxyEnable is null && stale.OriginalProxyServer is null)
            return $"ReqTree process {stale.ProcessId} took port {stale.Port} at {stale.StartedAt:u} "
                 + "and exited without cleaning up, but recorded no previous settings to restore. "
                 + "Check your proxy settings by hand.";

        try
        {
            if (!RestoreSettings((stale.OriginalProxyEnable, stale.OriginalProxyServer)))
                return $"Found stale proxy state from process {stale.ProcessId}, but this platform "
                     + "has no system proxy setting to restore.";

            ClearState();
            return $"Restored system proxy settings left behind by ReqTree process "
                 + $"{stale.ProcessId}, which had taken port {stale.Port} at {stale.StartedAt:u} "
                 + "and exited without cleaning up.";
        }
        catch (Exception ex)
        {
            return $"Found stale proxy state from process {stale.ProcessId} but could not restore "
                 + $"it: {ex.Message}. The system proxy may still point at port {stale.Port}.";
        }
    }

    public void Dispose() => ReleaseOwnership();

    private bool TryClaimOwnership()
    {
        if (_ownership is not null) return true;

        try
        {
            var ownership = new Semaphore(initialCount: 1, maximumCount: 1, name: OwnershipName);
            if (!ownership.WaitOne(TimeSpan.Zero))
            {
                ownership.Dispose();
                return false;
            }

            _ownership = ownership;
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException
                or PlatformNotSupportedException)
        {
            Log.Warning(ex,
                "Could not check whether another ReqTree owns the system proxy settings, so this "
                + "one is proceeding as though it does not. If two are running, stopping them in "
                + "the wrong order can leave the machine pointed at a dead port.");
            return true;
        }
    }

    private static bool TryReadSettings(out (int? Enable, string? Server) settings)
    {
        settings = (null, null);
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
            if (key is null) return true;

            settings = (key.GetValue("ProxyEnable") as int?, key.GetValue("ProxyServer") as string);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("Could not read the current system proxy settings: {Reason}", ex.Message);
            return false;
        }
    }

    private static bool RestoreSettings((int? Enable, string? Server) original)
    {
        if (!OperatingSystem.IsWindows()) return false;

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
        if (key is null) return false;

        if (original.Enable is { } enable)
            key.SetValue("ProxyEnable", enable, Microsoft.Win32.RegistryValueKind.DWord);
        else
            key.DeleteValue("ProxyEnable", throwOnMissingValue: false);

        if (original.Server is { } server)
            key.SetValue("ProxyServer", server, Microsoft.Win32.RegistryValueKind.String);
        else
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);

        NotifySettingsChanged();
        return true;
    }

    private static void NotifySettingsChanged()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            Internet.InternetSetOption(IntPtr.Zero, Internet.OptionSettingsChanged, IntPtr.Zero, 0);
            Internet.InternetSetOption(IntPtr.Zero, Internet.OptionRefresh, IntPtr.Zero, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // The registry write already happened. Refreshing running applications is best effort.
        }
    }

    private static void RecordState((int? Enable, string? Server) original, int port)
    {
        try
        {
            var marker = new ProxyStateMarker(
                Environment.ProcessId,
                new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()),
                port, original.Enable, original.Server);

            File.WriteAllText(DirectoryManager.ProxyStateFilePath, JsonSerializer.Serialize(marker));
        }
        catch (Exception ex)
        {
            Log.Warning("Could not record proxy state: {Reason}", ex.Message);
        }
    }

    private static void ClearState()
    {
        try
        {
            if (File.Exists(DirectoryManager.ProxyStateFilePath))
                File.Delete(DirectoryManager.ProxyStateFilePath);
        }
        catch (IOException)
        {
            // A leftover marker is harmless: startup checks whether its process is still alive.
        }
    }

    private static ProxyStateMarker? FindStaleState()
    {
        try
        {
            if (!File.Exists(DirectoryManager.ProxyStateFilePath)) return null;

            var marker = JsonSerializer.Deserialize<ProxyStateMarker>(
                File.ReadAllText(DirectoryManager.ProxyStateFilePath));

            if (marker is null || marker.ProcessId == Environment.ProcessId) return null;
            return IsMarkerProcessAlive(marker) ? null : marker;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static bool IsMarkerProcessAlive(ProxyStateMarker marker)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(marker.ProcessId);
            return !process.HasExited
                && process.StartTime.ToUniversalTime() == marker.StartedAt.UtcDateTime;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private sealed record ProxyStateMarker(
        int ProcessId,
        DateTimeOffset StartedAt,
        int Port,
        int? OriginalProxyEnable,
        string? OriginalProxyServer);
}

internal enum SystemProxyTakeover
{
    Taken,
    OwnedByAnotherReqTree,
}
