using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReqTree.App;
using ReqTree.Proxy.Objects;
using ReqTree.WinApi;
using Serilog;
using Serilog.Extensions.Logging;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;

namespace ReqTree.Proxy;

/// <summary>
/// Owns the Titanium.Web.Proxy instance: root certificate, listening endpoint, system proxy
/// registration, and the two hooks that will turn live traffic into captured exchanges.
/// </summary>
/// <remarks>
/// The Titanium server is built fresh in <see cref="TryStart"/> and disposed in <see cref="Stop"/>
/// rather than living for the lifetime of this object. That is what makes the proxy genuinely
/// restartable: a stopped-and-restarted ProxyServer would be asked to add an endpoint it already
/// has. Callers never see the swap, because the port and the state live on this class, not on the
/// server underneath.
///
/// Tracking whether we own the machine's proxy settings lives here too, as ordinary members. It is
/// this object's business — nothing else in the program can know it — and a separate holder for
/// four short methods would only put a class boundary between the flag and the file recording it.
/// </remarks>
public sealed class CaptureProxy : IAsyncDisposable
{
    /// <summary>
    /// What gets written to disk when we take over the machine's proxy settings, so a run that
    /// dies without cleaning up can be undone by the next one.
    /// </summary>
    /// <remarks>
    /// It carries the settings that were in place before we touched them, because that is the one
    /// thing a later process cannot work out for itself. Titanium's own
    /// RestoreOriginalProxySettings only knows what *its* instance replaced, so a fresh server in a
    /// new process restores nothing at all — and does it without complaining.
    /// </remarks>
    private sealed record ProxyStateMarker(
        int ProcessId,
        DateTimeOffset StartedAt,
        int Port,
        int? OriginalProxyEnable,
        string? OriginalProxyServer);

    /// <summary>Where Windows keeps the per-user proxy settings Titanium writes.</summary>
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private readonly bool _registerAsSystemProxy;
    private readonly bool _installCertificateTrust;

    /// <summary>Serialises start and stop, which can arrive concurrently once tools can call them.</summary>
    private readonly Lock _lifecycleLock = new();

    /// <summary>Null whenever the proxy is stopped.</summary>
    private ProxyServer? _proxyServer;
    private bool _systemProxyWasSet;

    /// <summary>
    /// What the machine's proxy settings were before we took them over. Held so <see cref="Stop"/>
    /// can put them back through the same code the crash-recovery path uses, rather than trusting
    /// Titanium to remember.
    /// </summary>
    private (int? Enable, string? Server) _originalSystemProxy;

    /// <summary>
    /// Everything this proxy has captured. The same shape a capture loaded from a file has, which
    /// is what the read side gets handed instead when there is no proxy running at all.
    /// </summary>
    public ExchangeStore Capture { get; }

    /// <summary>
    /// Numbers every exchange the proxy sees, recorded or not.
    /// </summary>
    /// <remarks>
    /// The proxy assigns ids rather than leaving it to the store, because rules and scripts run
    /// whether or not recording is on — and an unrecorded exchange with no id makes every line it
    /// logs say "exchange 0", which is exactly when you most need to tell them apart.
    /// </remarks>
    private long _nextExchangeId;

    /// <summary>
    /// Whether exchanges are being recorded. Turning it off leaves traffic flowing normally but
    /// stops it being kept, which is how a session avoids filling the capture with noise while the
    /// interesting part has not happened yet.
    /// </summary>
    /// <remarks>
    /// Volatile because it is read on every request from proxy threads and written from somewhere
    /// else entirely — an MCP tool. Without it a thread can go on reading a stale copy from a
    /// register and keep recording after being told to stop.
    /// </remarks>
    public bool CaptureEnabled
    {
        get => _captureEnabled;
        set => _captureEnabled = value;
    }

    private volatile bool _captureEnabled = true;

    // Ordered rather than hashed, because the order they run in is part of what a rule or script
    // means. See BehaviourList for why that rules out a HashSet.
    private readonly BehaviourList<Rule> _rules = new(rule => rule.Name);
    private readonly BehaviourList<Script> _scripts = new(script => script.Name);

    /// <summary>
    /// Named sets of scripts, each switched on or off as one. Rules are never in an environment —
    /// a script can express anything a rule can, so one collection is enough.
    /// </summary>
    private volatile Objects.Environment[] _environmentList = [];
    private readonly Lock _environmentLock = new();

    /// <summary>Every rule. Rules are a flat list; only scripts can belong to an environment.</summary>
    public IReadOnlyList<Rule> Rules => _rules.Items;

    /// <summary>Standalone scripts — those not belonging to any environment.</summary>
    public IReadOnlyList<Script> Scripts => _scripts.Items;

    /// <summary>The environments, in the order they were created.</summary>
    public IReadOnlyList<Objects.Environment> Environments => _environmentList;

    /// <summary>Every script anywhere, in the order it runs: environments first.</summary>
    public IReadOnlyList<(Script Script, string? Environment)> AllScripts =>
    [
        .. _environmentList.SelectMany(e => e.Scripts.Select(s => (s, (string?)e.Name))),
        .. _scripts.Items.Select(s => (s, (string?)null)),
    ];

    /// <summary>
    /// Captures opened from a file, by the name they were opened under. Held alongside the live
    /// one rather than replacing it, so opening yesterday's capture to compare against does not
    /// throw away what is being recorded now. Read tools name which one they mean.
    /// </summary>
    private readonly Dictionary<string, ExchangeStore> _opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _openedLock = new();

    /// <summary>Names of the captures opened from files.</summary>
    public IReadOnlyList<string> OpenedCaptures
    {
        get { lock (_openedLock) return [.. _opened.Keys]; }
    }

    /// <summary>Adds an opened capture under a name, replacing any already held under it.</summary>
    public void AddOpenedCapture(string name, ExchangeStore store)
    {
        lock (_openedLock) _opened[name] = store;
    }

    /// <summary>Forgets an opened capture. The live one cannot be closed.</summary>
    public bool CloseCapture(string name)
    {
        lock (_openedLock) return _opened.Remove(name);
    }

    /// <summary>
    /// The capture a read tool means. Null or "live" is the one being recorded; anything else is
    /// a file opened under that name.
    /// </summary>
    public ExchangeStore? ResolveCapture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Equals("live", StringComparison.OrdinalIgnoreCase))
            return Capture;

        lock (_openedLock) return _opened.GetValueOrDefault(name);
    }

    /// <summary>
    /// Raised once an exchange is finished, whether answered upstream or by a rule. The console
    /// view is the only thing that listens today.
    /// </summary>
    public event Action<Exchange>? ExchangeCompleted;

    /// <summary>
    /// Stops recording the first time an exchange matches. Null when no window is armed.
    /// </summary>
    /// <remarks>
    /// A capture window exists so a session can say "record until the sign-in POST happens" and
    /// then walk away, instead of watching for the moment to call stop_capture and catching an
    /// extra minute of background noise either side of it.
    ///
    /// Not volatile, deliberately: it is claimed with <see cref="Interlocked.CompareExchange{T}"/>,
    /// and a volatile field cannot be passed by reference. Reads go through
    /// <see cref="Volatile.Read{T}"/> instead.
    /// </remarks>
    private CaptureWindow? _captureWindow;

    /// <summary>An armed stop condition, and what it was described as.</summary>
    public sealed record CaptureWindow(Func<Exchange, bool> StopWhen, string Description, string? ArmedBy);

    /// <summary>What is armed right now, or null.</summary>
    public CaptureWindow? ArmedWindow => Volatile.Read(ref _captureWindow);

    /// <summary>Arms a stop condition, replacing any already armed.</summary>
    public void ArmCaptureWindow(CaptureWindow window)
    {
        Volatile.Write(ref _captureWindow, window);
        Log.Information("Capture window armed by {Actor}: recording stops when {Description}.",
            window.ArmedBy ?? "unidentified", window.Description);
    }

    /// <summary>Disarms the window. Returns what was armed, or null if nothing was.</summary>
    public CaptureWindow? DisarmCaptureWindow() => Interlocked.Exchange(ref _captureWindow, null);

    /// <summary>Port this proxy listens on.</summary>
    public int Port { get; }

    /// <summary>True while the proxy is listening.</summary>
    public bool IsRunning => _proxyServer?.ProxyRunning ?? false;

    /// <summary>True when we have pointed the machine's proxy settings at ourselves.</summary>
    public bool IsSystemProxy => _systemProxyWasSet;

    /// <param name="port">TCP port to listen on.</param>
    /// <param name="registerAsSystemProxy">
    /// When false, ReqTree only listens and the caller points clients at it manually. Useful for
    /// testing, and ignored on platforms with no system-wide proxy setting.
    /// </param>
    /// <param name="installCertificateTrust">
    /// When false, the root certificate is still generated and exported, but not added to this
    /// machine's trust store. Clients then have to be pointed at the exported .cer themselves.
    /// </param>
    /// <param name="capture">
    /// The store to record into, carrying whatever caps the command line asked for. Left null it is
    /// unbounded, which is right for a test but not for a long session on a real machine.
    /// </param>
    public CaptureProxy(
        int port,
        bool registerAsSystemProxy = true,
        bool installCertificateTrust = true,
        ExchangeStore? capture = null)
    {
        Port = port;
        _registerAsSystemProxy = registerAsSystemProxy;
        _installCertificateTrust = installCertificateTrust;
        Capture = capture ?? new ExchangeStore();

        // A cap that stops recording has to turn capture off as well, or the hooks carry on
        // building exchanges the store then refuses one at a time.
        Capture.LimitReached += reason =>
        {
            _captureEnabled = false;
            Log.Warning("{Reason}", reason);
        };
    }

    /// <summary>
    /// Generates and trusts the root certificate if needed, starts listening, and points the
    /// machine's proxy settings at us.
    /// </summary>
    /// <remarks>
    /// Try-shaped because a busy port or a refused certificate prompt is an ordinary outcome, not
    /// an exceptional one: the MCP server is still worth having, and the proxy can be started again
    /// later once the problem is fixed. The reason is logged rather than thrown.
    /// </remarks>
    /// <returns>True when the proxy is listening once this returns, including if it already was.</returns>
    public bool TryStart()
    {
        lock (_lifecycleLock)
        {
            if (_proxyServer is not null && _proxyServer.ProxyRunning)
            {
                Log.Debug("Proxy is already listening on port {Port}.", Port);
                return true;
            }

            ProxyServer? server = null;

            try
            {
                server = BuildServer();
                var endPoint = new ExplicitProxyEndPoint(IPAddress.Any, Port, decryptSsl: true);

                // Whether the .pfx already exists is our "is this a first run?" signal. Asking the
                // certificate manager instead would mean querying a root certificate it has not
                // loaded yet, which throws.
                if (_installCertificateTrust && !File.Exists(DirectoryManager.RootCertificatePath))
                    Log.Information(
                        "Generating the ReqTree root certificate - approve the trust prompt if one appears.");

                // Creates the root CA on first run and, when asked, adds it to the user trust
                // store. A cheap no-op afterwards, so it is safe to call on every start.
                server.CertificateManager.EnsureRootCertificate();
                ExportRootCertificate(server);

                server.AddEndPoint(endPoint);
                server.Start(changeSystemProxySettings: false);

                // Only Windows has a system-wide proxy setting Titanium can write. Elsewhere the
                // user points their client at us manually; capture works identically either way.
                if (_registerAsSystemProxy && OperatingSystem.IsWindows())
                {
                    // Read before the takeover, or what we record is our own settings and the
                    // recovery path faithfully restores the broken state.
                    _originalSystemProxy = ReadSystemProxySettings();

                    server.SetAsSystemProxy(endPoint, ProxyProtocolType.AllHttp);
                    _systemProxyWasSet = true;

                    // Written immediately after taking over, so a crash between here and shutdown
                    // still leaves enough behind for the next run to undo it.
                    RecordProxyState(_originalSystemProxy);
                }

                _proxyServer = server;

                Log.Information("Proxy listening on port {Port}. System proxy {SystemProxy}.",
                    Port, _systemProxyWasSet ? "points at ReqTree" : "was not changed");

                return true;
            }
            catch (Exception ex)
            {
                // Two separate attempts, not one. Sharing a try meant a registry failure skipped
                // the teardown below it — leaving a half-started proxy still holding its listener
                // and never disposed, on top of the settings being wrong. Each has to be able to
                // fail without taking the other with it.
                if (_systemProxyWasSet)
                {
                    try
                    {
                        RestoreSystemProxySettings(_originalSystemProxy);
                        _systemProxyWasSet = false;
                        ClearProxyState();
                    }
                    catch (Exception restoreFailure)
                    {
                        // Marker deliberately left in place so the next run repairs it.
                        Log.Error(restoreFailure,
                            "The proxy failed to start AND the machine's proxy settings could not "
                            + "be put back. They may still point at port {Port}. Starting ReqTree "
                            + "again will repair them.", Port);
                    }
                }

                // Whatever was half-built has to go, or the next attempt inherits a server that
                // already holds the endpoint and fails for a second, more confusing reason.
                if (server is not null)
                {
                    try
                    {
                        if (server.ProxyRunning) server.Stop();
                        server.Dispose();
                    }
                    catch (Exception cleanupFailure)
                    {
                        Log.Warning(cleanupFailure, "Could not clean up after a failed proxy start.");
                    }
                }

                _proxyServer = null;
                Log.Error(ex, "Could not start the proxy on port {Port}.", Port);
                return false;
            }
        }
    }

    /// <summary>
    /// Restores the machine's proxy settings and stops listening. Safe to call more than once,
    /// which matters because several shutdown paths can race to call it.
    /// </summary>
    /// <returns>True if this call stopped a running proxy, false if it was already down.</returns>
    public bool Stop()
    {
        lock (_lifecycleLock)
        {
            var server = _proxyServer;
            if (server is null) return false;

            // Captured before the restore clears it, so the message below can say what actually
            // happened rather than claiming a restore that never took place.
            var hadSystemProxy = _systemProxyWasSet;

            if (_systemProxyWasSet)
            {
                try
                {
                    // Writes back exactly what was there before we started, rather than blanket-
                    // disabling the proxy and clobbering a real corporate setting. This is the same
                    // code the crash-recovery path uses; Titanium's own RestoreOriginalProxySettings
                    // is deliberately not used, because it reports success even when it restores
                    // nothing, and one mechanism that is known to work beats two that might.
                    RestoreSystemProxySettings(_originalSystemProxy);
                    _systemProxyWasSet = false;

                    // Cleared only after the restore succeeded, so the marker outliving us always
                    // means the settings really are still ours.
                    ClearProxyState();
                }
                catch (Exception ex)
                {
                    // Caught so that failing to restore does not also stop us releasing the port.
                    // Left unguarded this threw out of Stop, which is called from a finally block —
                    // so the listener stayed up, the MCP host was never shut down, and the log was
                    // never flushed, on top of the settings still being wrong.
                    //
                    // The marker is deliberately NOT cleared: it is now the only record that this
                    // machine needs repairing, and the next run reads it and puts things back.
                    Log.Error(ex,
                        "Could not restore the machine's proxy settings. They still point at "
                        + "ReqTree on port {Port}, so applications will lose connectivity when this "
                        + "process exits. Starting ReqTree again will repair it, or set them back "
                        + "by hand in Internet Options.", Port);
                }
            }

            var wasRunning = server.ProxyRunning;
            if (wasRunning) server.Stop();

            // Disposed rather than kept, so the next start builds a clean server instead of reusing
            // one that already holds an endpoint.
            server.Dispose();
            _proxyServer = null;

            // Reported from what actually happened, not from what was attempted. _systemProxyWasSet
            // is cleared only by a restore that succeeded, so it still being set here means the
            // catch above ran — and saying "restored" then would be the same false-success claim
            // that made the original Titanium restore so dangerous.
            if (!hadSystemProxy)
                Log.Information("Proxy stopped. System proxy settings were never changed.");
            else if (!_systemProxyWasSet)
                Log.Information("Proxy stopped and system proxy settings restored.");
            else
                Log.Error("Proxy stopped, but the machine's proxy settings could NOT be restored "
                        + "and still point at port {Port}. Run ReqTree again to repair them.", Port);

            return wasRunning;
        }
    }

    /// <summary>
    /// Undoes system proxy settings left behind by a run that died without cleaning up.
    /// </summary>
    /// <remarks>
    /// ReqTree's most damaging possible failure is not losing a capture — it is dying without
    /// restoring the system proxy, which leaves every application on the machine pointed at a port
    /// nothing is listening on. To the user, the internet has simply stopped working, with no
    /// visible cause. <see cref="Stop"/> covers the ordinary exits; this covers a power cut, a hard
    /// kill, or a crash.
    ///
    /// It writes the settings back itself, from the marker, rather than asking Titanium to do it.
    /// Titanium restores only what its own instance replaced, so a fresh server in a new process
    /// restores nothing — and reports success while doing it. Deliberately independent of this
    /// instance's own state, so it is safe to call before <see cref="TryStart"/> and it never
    /// disturbs a proxy this object already has running.
    ///
    /// The marker is cleared only when the settings really were put back. Clearing it after a
    /// failed restore would destroy the only record that anything is wrong, and no later run would
    /// ever notice.
    /// </remarks>
    /// <returns>A description of what was cleaned up, or null when there was nothing stale.</returns>
    public string? CleanStaleState()
    {
        var stale = FindStaleProxyState();
        if (stale is null) return null;

        // A marker with nothing recorded in it cannot be acted on: we would be guessing at settings
        // the user may have chosen deliberately. Say so plainly and leave it alone.
        if (stale.OriginalProxyEnable is null && stale.OriginalProxyServer is null)
            return $"ReqTree process {stale.ProcessId} took port {stale.Port} at {stale.StartedAt:u} "
                 + "and exited without cleaning up, but recorded no previous settings to restore. "
                 + "Check your proxy settings by hand.";

        try
        {
            if (!RestoreSystemProxySettings((stale.OriginalProxyEnable, stale.OriginalProxyServer)))
                return $"Found stale proxy state from process {stale.ProcessId}, but this platform "
                     + "has no system proxy setting to restore.";

            ClearProxyState();

            return $"Restored system proxy settings left behind by ReqTree process "
                 + $"{stale.ProcessId}, which had taken port {stale.Port} at {stale.StartedAt:u} "
                 + "and exited without cleaning up.";
        }
        catch (Exception ex)
        {
            // Reported rather than thrown: the caller is usually startup, and failing to heal is
            // not a reason to refuse to run. The marker stays, so the next run tries again.
            return $"Found stale proxy state from process {stale.ProcessId} but could not restore "
                 + $"it: {ex.Message}. The system proxy may still point at port {stale.Port}.";
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------------------------
    // Rules and scripts
    // ---------------------------------------------------------------------------------

    /// <summary>Adds a rule, replacing any existing one with the same name.</summary>
    /// <returns>True when it replaced one, false when it was new.</returns>
    public bool AddRule(Rule rule)
    {
        var replaced = _rules.AddOrReplace(rule);

        Log.Information(
            "Rule {Rule} {Action} by {Actor}: {Description} {Count} rule(s) now, evaluated in the "
            + "order they were added and all before any script.",
            rule.Name, replaced ? "replaced" : "added", rule.AddedBy ?? "unidentified",
            rule.Description ?? "(no description given).", _rules.Count);

        return replaced;
    }

    /// <summary>Removes a rule by name.</summary>
    public bool RemoveRule(string name, string? actor = null)
    {
        if (!_rules.Remove(name)) return false;

        Log.Information("Rule {Rule} removed by {Actor}. {Count} rule(s) left.",
            name, actor ?? "unidentified", _rules.Count);

        return true;
    }

    /// <summary>
    /// Adds a script, replacing any of the same name in the same place.
    /// </summary>
    /// <param name="environment">
    /// The environment it belongs to, created if it does not exist yet. Null puts it in the
    /// standalone list, which runs after every environment.
    /// </param>
    /// <returns>True when it replaced one, false when it was new.</returns>
    public bool AddScript(Script script, string? environment = null)
    {
        bool replaced;
        int count;

        if (environment is null)
        {
            replaced = _scripts.AddOrReplace(script);
            count = _scripts.Count;
        }
        else
        {
            var target = EnvironmentFor(environment, script.AddedBy);
            replaced = target.AddOrReplace(script);
            count = target.Scripts.Count;
        }

        Log.Information(
            "Script {Script} {Action} by {Actor} in {Where} on hook {Hook}. {Count} script(s) there now.",
            script.Name, replaced ? "replaced" : "added", script.AddedBy ?? "unidentified",
            environment is null ? "the standalone list" : $"environment '{environment}'",
            script.Hook, count);

        return replaced;
    }

    /// <summary>Removes a script by name, from wherever it is.</summary>
    public bool RemoveScript(string name, string? actor = null)
    {
        if (_scripts.Remove(name))
        {
            Log.Information("Script {Script} removed by {Actor}. {Count} standalone script(s) left.",
                name, actor ?? "unidentified", _scripts.Count);
            return true;
        }

        foreach (var environment in _environmentList)
            if (environment.Remove(name))
            {
                Log.Information("Script {Script} removed from environment {Environment} by {Actor}.",
                    name, environment.Name, actor ?? "unidentified");
                return true;
            }

        return false;
    }

    /// <summary>The environment of this name, creating it if it is new.</summary>
    private Objects.Environment EnvironmentFor(string name, string? actor)
    {
        var trimmed = name.Trim();

        lock (_environmentLock)
        {
            if (Array.Find(_environmentList, e => Same(e.Name, trimmed)) is { } existing)
                return existing;

            var created = new Objects.Environment { Name = trimmed, AddedBy = actor };
            _environmentList = [.. _environmentList, created];

            Log.Information("Environment {Environment} created by {Actor}. Its scripts run before "
                + "the standalone ones.", trimmed, actor ?? "unidentified");

            return created;
        }
    }

    /// <summary>Turns a rule or script on or off by name.</summary>
    /// <remarks>Names match trimmed and case-insensitively, exactly as <see cref="BehaviourList{T}"/> does.</remarks>
    public bool SetRuleEnabled(string name, bool enabled, string? actor = null)
    {
        var rule = Array.Find(_rules.Items, r => Same(r.Name, name));
        if (rule is null) return false;

        rule.Enabled = enabled;
        Log.Information("Rule {Rule} {State} by {Actor}.",
            name, enabled ? "enabled" : "disabled", actor ?? "unidentified");

        return true;
    }

    /// <inheritdoc cref="SetRuleEnabled"/>
    public bool SetScriptEnabled(string name, bool enabled, string? actor = null)
    {
        var script = AllScripts.Select(s => s.Script).FirstOrDefault(s => Same(s.Name, name));
        if (script is null) return false;

        script.Enabled = enabled;
        Log.Information("Script {Script} {State} by {Actor}.",
            name, enabled ? "enabled" : "disabled", actor ?? "unidentified");

        return true;
    }

    // ---------------------------------------------------------------------------------
    // Environments — a name shared by a group of rules and scripts, and nothing more
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Turns a whole environment on or off. Nothing inside it is touched — the environment itself
    /// is simply skipped or not, which is the point of it owning its own contents.
    /// </summary>
    /// <returns>What it holds, or null when there is no environment of that name.</returns>
    public int? SetEnvironmentEnabled(string name, bool enabled, string? actor = null)
    {
        if (Array.Find(_environmentList, e => Same(e.Name, name)) is not { } environment)
            return null;

        environment.Enabled = enabled;

        Log.Information("Environment {Environment} {State} by {Actor}: {Scripts} script(s).",
            environment.Name, enabled ? "enabled" : "disabled", actor ?? "unidentified",
            environment.Scripts.Count);

        return environment.Scripts.Count;
    }

    /// <summary>Drops an environment and every script in it.</summary>
    /// <returns>How many scripts went with it, or null when there was no such environment.</returns>
    public int? RemoveEnvironment(string name, string? actor = null)
    {
        lock (_environmentLock)
        {
            if (Array.Find(_environmentList, e => Same(e.Name, name)) is not { } environment)
                return null;

            var count = environment.Scripts.Count;
            _environmentList = Array.FindAll(_environmentList, e => !Same(e.Name, name));

            Log.Information(
                "Environment {Environment} unloaded by {Actor}: {Scripts} script(s) went with it. "
                + "{Left} environment(s) left.",
                environment.Name, actor ?? "unidentified", count, _environmentList.Length);

            return count;
        }
    }

    /// <summary>
    /// Name comparison used everywhere: trimmed and case-insensitive, so a name padded once and
    /// not the next time still refers to the same thing.
    /// </summary>
    private static bool Same(string? a, string? b) =>
        a is not null && b is not null
        && a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs every matching rule, then every script for the hook, against one exchange.
    /// </summary>
    /// <remarks>
    /// Rules first, always. They are the declarative path and the one another session can read and
    /// reason about; a script is for what rules cannot express, so it gets the last word rather
    /// than the first.
    ///
    /// Neither is allowed to break traffic. Whatever a rule or script throws is caught, logged
    /// against its name, and the next one runs — a bad rule costs its own effect and nothing else.
    /// </remarks>
    private void ApplyBehaviour(Exchange exchange, ProxyHook hook)
    {
        // Rules are request-phase only: their whole point is deciding what happens to a request
        // before it goes anywhere.
        // Whoever answered the request, so a later change to it can be reported rather than passed
        // over. Nothing is blocked: overriding is allowed, it just does not happen silently.
        string? answeredBy = null;

        if (hook is ProxyHook.BeforeRequest)
        {
            // Taken once, so the whole evaluation runs over one consistent set even if another
            // session adds or removes a rule halfway through.
            var rules = _rules.Items;
            var matched = 0;

            foreach (var rule in rules)
            {
                if (!rule.Enabled) continue;

                bool applies;

                try
                {
                    applies = rule.Condition(exchange);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "Rule {Rule} (added by {Actor}) threw while testing {Method} {Url} "
                        + "(exchange {Id}). Treated as no match.",
                        rule.Name, rule.AddedBy ?? "unidentified",
                        exchange.Method, exchange.Url, exchange.Id);
                    continue;
                }

                if (!applies)
                {
                    Log.Verbose("Rule {Rule} did not match {Method} {Url}.",
                        rule.Name, exchange.Method, exchange.Url);
                    continue;
                }

                matched++;
                rule.RecordHit();

                var statusBefore = exchange.StatusCode;

                // Read before the action runs. A redirect rewrites Url, so logging it afterwards
                // reports the destination as though that were what the condition matched.
                var matchedUrl = exchange.Url;

                try
                {
                    rule.Action(exchange);

                    Log.Information(
                        "Rule {Rule} (added by {Actor}) matched {Method} {Url} (exchange {Id}) "
                        + "and ran. Hit {HitCount} time(s) so far.",
                        rule.Name, rule.AddedBy ?? "unidentified",
                        exchange.Method, matchedUrl, exchange.Id, rule.HitCount);

                    answeredBy = NoteAnswer(exchange, statusBefore, answeredBy, "Rule", rule.Name, null);
                }
                catch (Exception ex)
                {
                    // Not "the exchange is unchanged": an action that throws part-way through has
                    // already applied whatever it did before throwing, and there is no undo. Saying
                    // otherwise would send whoever is debugging this in the wrong direction.
                    Log.Warning(ex,
                        "Rule {Rule} (added by {Actor}) matched {Method} {Url} (exchange {Id}) "
                        + "but its action threw part-way through. Anything it had already changed "
                        + "on the exchange stands; the rest was not applied. Status is now {Status}.",
                        rule.Name, rule.AddedBy ?? "unidentified",
                        exchange.Method, matchedUrl, exchange.Id,
                        exchange.StatusCode?.ToString() ?? "unset");
                }
            }

            if (rules.Length > 0)
                Log.Debug("Evaluated {Count} rule(s) against {Method} {Url}; {Matched} matched.",
                    rules.Length, exchange.Method, exchange.Url, matched);
        }

        // Enabled environments first, in order, then the standalone scripts. A disabled environment
        // is skipped whole rather than filtered script by script — that is what owning its own list
        // buys.
        var scripts = new List<(Script Script, string? Environment)>();

        foreach (var environment in _environmentList)
            if (environment.Enabled)
                foreach (var script in environment.Scripts)
                    scripts.Add((script, environment.Name));

        foreach (var script in _scripts.Items)
            scripts.Add((script, null));

        foreach (var (script, fromEnvironment) in scripts)
        {
            if (!script.Enabled || script.Hook != hook) continue;

            var statusBefore = exchange.StatusCode;

            try
            {
                script.Run(exchange);
                script.RecordRun();

                Log.Information(
                    "Script {Script} (added by {Actor}) ran on {Hook} for {Method} {Url} "
                    + "(exchange {Id}). {RunCount} run(s), {ErrorCount} error(s) so far.",
                    script.Name, script.AddedBy ?? "unidentified", hook,
                    exchange.Method, exchange.Url, exchange.Id, script.RunCount, script.ErrorCount);

                // Only on the request hook. On the response hook a status is always already set —
                // it is the one the server actually sent — so every script would look like an
                // override.
                if (hook is ProxyHook.BeforeRequest)
                    answeredBy = NoteAnswer(
                        exchange, statusBefore, answeredBy, "Script", script.Name, fromEnvironment);
            }
            catch (Exception ex)
            {
                script.RecordError();

                Log.Warning(ex,
                    "Script {Script} (added by {Actor}) threw on {Hook} for {Method} {Url}. "
                    + "Traffic is unaffected; it has now failed {ErrorCount} time(s).",
                    script.Name, script.AddedBy ?? "unidentified", hook,
                    exchange.Method, exchange.Url, script.ErrorCount);
            }
        }
    }

    /// <summary>
    /// Tracks who answered a request, and says so when something later changes that answer.
    /// </summary>
    /// <remarks>
    /// Overriding is allowed on purpose — a session that wants the last word should be able to take
    /// it without editing somebody else's environment. What it must not be is silent: an
    /// environment script answering with one status and a standalone script quietly replacing it is
    /// exactly the kind of thing that looks like the environment simply not working. The warning
    /// names both and points at the fix, which is to change the environment rather than shadow it.
    /// </remarks>
    /// <returns>Who now owns the answer.</returns>
    private static string? NoteAnswer(
        Exchange exchange, int? statusBefore, string? answeredBy,
        string kind, string name, string? environment)
    {
        if (exchange.StatusCode is null || exchange.StatusCode == statusBefore)
            return answeredBy;

        var who = environment is null ? $"{kind} {name}" : $"{kind} {name} (environment {environment})";

        if (answeredBy is null)
        {
            Log.Debug("{Who} answered exchange {Id} with {Status}.", who, exchange.Id,
                exchange.StatusCode);
            return who;
        }

        Log.Warning(
            "{Who} overrode the answer {Earlier} had already given for {Method} {Url} "
            + "(exchange {Id}): {Before} became {After}. This is allowed, and the last one to run "
            + "wins. If that is not what you meant, change the earlier one rather than adding "
            + "something after it.",
            who, answeredBy, exchange.Method, exchange.Url, exchange.Id,
            statusBefore?.ToString() ?? "none", exchange.StatusCode);

        return who;
    }

    // ---------------------------------------------------------------------------------
    // Titanium wiring
    // ---------------------------------------------------------------------------------

    /// <summary>Creates a Titanium server configured the way ReqTree needs it.</summary>
    /// <remarks>
    /// Everything after the constructor is wrapped, because a failure while configuring would
    /// otherwise leave the newly built server unreachable and undisposed — the caller never gets it
    /// and so never cleans it up. The same shape as the guards in McpEndpoint and CaptureFile.
    /// </remarks>
    private ProxyServer BuildServer()
    {
        var server = CreateServer();

        try
        {
            Configure(server);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The first flag asks Titanium to install our generated root CA into the *current user's*
    /// trust store. Machine-wide trust would need an elevated process, and per-user is enough to
    /// decrypt this user's own browsers and apps.
    /// </summary>
    private ProxyServer CreateServer() => new(
        userTrustRootCertificate: _installCertificateTrust,
        machineTrustRootCertificate: false,
        trustRootCertificateAsAdmin: false);

    private void Configure(ProxyServer server)
    {
        server.CertificateManager.RootCertificateName = "ReqTree Root Certificate Authority";
        server.CertificateManager.RootCertificateIssuerName = "ReqTree";
        server.CertificateManager.PfxFilePath = DirectoryManager.RootCertificatePath;

        // Set explicitly because 5.x defaults it on. Off means Titanium negotiates HTTP/1.1 over
        // ALPN on the client-facing leg, so the capture hooks only ever see one wire format. This
        // is the flag to revisit once capture can handle h2 framing — the library supports it, we
        // do not yet, and turning it on before then would mean capturing traffic we cannot read.
        server.EnableHttp2 = false;

        // Titanium never throws from inside its own pipeline; it logs. Left alone it would write
        // its own coloured output straight to the console, which means two logging systems sharing
        // a terminal and nothing reaching ReqTree's log file. Pointing its factory at Serilog puts
        // proxy failures in the same place as everything else.
        server.Logging = new ProxyLoggingOptions
        {
            Enabled = true,

            // Titanium's own default, and the right one here: it raises an exception for every
            // dropped connection and failed TLS handshake, which is constant background noise on
            // any real machine rather than a sign that anything is wrong.
            MinimumLevel = LogLevel.Error,

            // Serilog owns both sinks. Leaving these on would duplicate every event.
            EnableConsole = false,
            EnableFile = false,

            // No argument, so it writes through Serilog's static Log — the same logger the rest of
            // ReqTree uses.
            LoggerFactory = new SerilogLoggerFactory(),
        };

        // The options above are only read when this is called; setting the property alone leaves
        // the server on the logger it built at construction.
        server.ApplyLoggingConfiguration();

        server.BeforeRequest += OnBeforeRequestAsync;
        server.BeforeResponse += OnBeforeResponseAsync;
    }

    /// <summary>
    /// Writes the public half of the root certificate to a .cer file. This is what a user hands to
    /// curl (--cacert), a phone, or any client whose trust store we cannot write to.
    /// </summary>
    /// <remarks>
    /// Failing to write it is logged and otherwise ignored. It is a convenience for pointing other
    /// clients at ReqTree, not something capture depends on, and refusing to start the proxy
    /// because a file most users never open could not be written would trade the whole feature for
    /// none of it.
    /// </remarks>
    private static void ExportRootCertificate(ProxyServer server)
    {
        var certificate = server.CertificateManager.RootCertificate;
        if (certificate is null) return;

        try
        {
            File.WriteAllBytes(
                DirectoryManager.RootCertificateCerPath,
                certificate.Export(X509ContentType.Cert));
        }
        catch (Exception ex)
        {
            Log.Warning(
                "Could not write the root certificate to {Path} ({Reason}). Capture is unaffected, "
                + "but there is no exported .cer to point another client or device at.",
                DirectoryManager.RootCertificateCerPath, ex.Message);
        }
    }

    /// <summary>Sees every request before it goes upstream.</summary>
    private async Task OnBeforeRequestAsync(object sender, SessionEventArgs e)
    {
        var request = e.HttpClient.Request;
        var uri = request.RequestUri;

        var exchange = new Exchange
        {
            // Numbered here rather than by the store, so it has an id even when recording is off
            // and every line a rule or script logs about it can name it.
            Id = Interlocked.Increment(ref _nextExchangeId),
            StartedAt = DateTimeOffset.Now,
            Method = request.Method,
            Url = request.Url,
            Host = uri.Host,
            Path = uri.AbsolutePath,
            QueryString = uri.Query,
            HttpVersion = request.HttpVersion.ToString(),
            RequestHeaders = ReadHeaders(request.Headers),
            RequestContentType = request.ContentType,
        };

        // Titanium hands us the same SessionEventArgs object again when the response arrives, so
        // parking the exchange here is how the two halves find each other.
        e.UserData = exchange;

        if (request.HasBody && ShouldCaptureExchange(request.ContentType, request.ContentLength))
        {
            try
            {
                // KeepBody has to be set before the body is read, otherwise Titanium streams it
                // upstream and discards it, and GetRequestBody comes back empty.
                request.KeepBody = true;
                (exchange.RequestBody, exchange.RequestBodyTruncated) = Truncate(await e.GetRequestBody());
            }
            catch (Exception ex)
            {
                // A body we cannot read is not a reason to drop the request from the capture.
                Log.Debug("Could not read the request body for {Url}: {Reason}", request.Url, ex.Message);
            }
        }

        // Recorded before behaviour runs, so a request a rule blocks is still captured — being
        // able to see what you blocked is the whole point of blocking it here rather than at a
        // firewall. Recording being off does not stop rules and scripts below: capture is about
        // what is kept, not about what ReqTree does to traffic.
        if (_captureEnabled)
        {
            Capture.AddExchange(exchange);

            // Checked after the exchange is stored, so the request that closes a window is itself
            // captured. Stopping on "the sign-in POST" and then not having it would be useless.
            CheckCaptureWindow(exchange);
        }

        // Behaviour runs after the body is read, so a condition can match on body content.
        var originalUrl = exchange.Url;
        var originalHeaders = exchange.RequestHeaders;
        var originalBody = exchange.RequestBody;

        ApplyBehaviour(exchange, ProxyHook.BeforeRequest);

        // Again, so the record shows what ReqTree actually did rather than what the client sent.
        // AddExchange recognises the id from the first call and updates in place.
        if (_captureEnabled) Capture.AddExchange(exchange);

        // A response set during the request hook means "answer this yourself" - the request never
        // goes upstream. That is how block and mock work without a separate concept for either.
        if (exchange.StatusCode is not null)
        {
            exchange.CompletedAt ??= DateTimeOffset.Now;

            var headers = new List<HttpHeader>
            {
                new("Content-Type", exchange.ResponseContentType ?? "application/json"),
            };

            if (exchange.ResponseHeaders is { } extra)
                foreach (var (name, value) in extra)
                    if (!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        headers.Add(new HttpHeader(name, value));

            e.GenericResponse(
                exchange.ResponseBody ?? [],
                (HttpStatusCode)exchange.StatusCode.Value,
                headers,
                closeServerConnection: true);

            // Cleared so the response hook knows there is nothing left to complete: this exchange
            // was answered here and never had a real response to wait for.
            e.UserData = null;

            Log.Information(
                "Answered {Method} {Url} locally with {Status} ({Bytes} byte body); it was not "
                + "sent upstream. Exchange {Id}.",
                exchange.Method, exchange.Url, exchange.StatusCode,
                exchange.ResponseBody?.Length ?? 0, exchange.Id);

            // Raised here too: from the outside this exchange is finished, and a console view that
            // showed only the ones that went upstream would silently omit everything a rule blocked.
            ExchangeCompleted?.Invoke(exchange);
            return;
        }

        WriteRequestChangesBack(exchange, e, originalUrl, originalHeaders, originalBody);
    }

    /// <summary>
    /// Pushes anything a rule or script changed on the exchange onto the request about to go out.
    /// </summary>
    /// <remarks>
    /// Compared against what arrived rather than applied unconditionally, so an untouched request
    /// is passed through byte for byte and only deliberate edits reach the wire.
    /// </remarks>
    private static void WriteRequestChangesBack(
        Exchange exchange,
        SessionEventArgs e,
        string originalUrl,
        IReadOnlyList<(string Name, string Value)> originalHeaders,
        byte[]? originalBody)
    {
        var request = e.HttpClient.Request;

        if (!string.Equals(exchange.Url, originalUrl, StringComparison.Ordinal))
        {
            request.Url = exchange.Url;

            // The Host header still names the original server, which most servers reject as a
            // mismatched virtual host. Replacing it is what makes a redirect actually work; there
            // is no set-or-add on this collection, so remove then add.
            if (Uri.TryCreate(exchange.Url, UriKind.Absolute, out var target))
            {
                request.Headers.RemoveHeader("Host");
                request.Headers.AddHeader("Host", target.Authority);
            }

            Log.Information("Request {Id} redirected from {From} to {To}.",
                exchange.Id, originalUrl, exchange.Url);
        }

        if (!ReferenceEquals(exchange.RequestHeaders, originalHeaders))
        {
            foreach (var (name, _) in originalHeaders)
                request.Headers.RemoveHeader(name);

            foreach (var (name, value) in exchange.RequestHeaders)
            {
                request.Headers.RemoveHeader(name);
                request.Headers.AddHeader(name, value);
            }

            Log.Information("Request {Id} headers rewritten: {Before} became {After} header(s).",
                exchange.Id, originalHeaders.Count, exchange.RequestHeaders.Count);
        }

        if (!ReferenceEquals(exchange.RequestBody, originalBody))
        {
            // Goes through Titanium rather than touching the bytes directly, because this is what
            // rewrites Content-Length to match.
            e.SetRequestBody(exchange.RequestBody ?? []);

            Log.Information("Request {Id} body rewritten: {Before} bytes became {After} bytes.",
                exchange.Id, originalBody?.Length ?? 0, exchange.RequestBody?.Length ?? 0);
        }
    }

    /// <summary>Sees every response before it reaches the client.</summary>
    private async Task OnBeforeResponseAsync(object sender, SessionEventArgs e)
    {
        // Missing UserData means we never saw the request half — a response the proxy synthesised
        // itself, say — so there is nothing to complete.
        if (e.UserData is not Exchange exchange)
            return;

        var response = e.HttpClient.Response;

        exchange.CompletedAt = DateTimeOffset.Now;
        exchange.StatusCode = response.StatusCode;
        exchange.ResponseHeaders = ReadHeaders(response.Headers);
        exchange.ResponseContentType = response.ContentType;
        exchange.ResponseSizeBytes = response.ContentLength > 0 ? response.ContentLength : 0;

        if (response.HasBody && ShouldCaptureExchange(response.ContentType, response.ContentLength))
        {
            try
            {
                response.KeepBody = true;

                // Titanium hands back the decoded body, so gzip and brotli responses arrive
                // readable rather than as a wall of binary.
                var body = await e.GetResponseBody();
                (exchange.ResponseBody, exchange.ResponseBodyTruncated) = Truncate(body);

                // A chunked response has no Content-Length, so what was actually read is the only
                // honest size to report.
                if (exchange.ResponseSizeBytes == 0)
                    exchange.ResponseSizeBytes = body.Length;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read the response body for {Url}: {Reason}", exchange.Url, ex.Message);
            }
        }

        var originalStatus = exchange.StatusCode;
        var originalHeaders = exchange.ResponseHeaders;
        var originalBody = exchange.ResponseBody;

        // Scripts see the response after it is captured, so one rewriting a body cannot change
        // what was recorded - the capture stays a record of what the server actually sent.
        ApplyBehaviour(exchange, ProxyHook.BeforeResponse);

        // The same exchange, now with its response half. AddExchange recognises it by the id it was
        // given on the way in and updates rather than filing the request a second time. It refuses
        // if the request half was dropped to stay within the caps, which is correct — half an
        // exchange, with a response and no request, is worse than none.
        if (_captureEnabled) Capture.AddExchange(exchange);

        if (exchange.StatusCode != originalStatus && exchange.StatusCode is { } status)
        {
            response.StatusCode = status;
            Log.Information("Response {Id} status rewritten from {From} to {To}.",
                exchange.Id, originalStatus, status);
        }

        if (!ReferenceEquals(exchange.ResponseHeaders, originalHeaders)
            && exchange.ResponseHeaders is { } headers)
        {
            // Everything that was there is removed first, exactly as the request path does. Adding
            // only what is in the new list would mean a script could add or change a response
            // header but never remove one — and it would look like it had.
            if (originalHeaders is not null)
                foreach (var (name, _) in originalHeaders)
                    response.Headers.RemoveHeader(name);

            foreach (var (name, value) in headers)
            {
                response.Headers.RemoveHeader(name);
                response.Headers.AddHeader(name, value);
            }

            Log.Information("Response {Id} headers rewritten: {Before} became {After} header(s).",
                exchange.Id, originalHeaders?.Count ?? 0, headers.Count);
        }

        if (!ReferenceEquals(exchange.ResponseBody, originalBody))
        {
            e.SetResponseBody(exchange.ResponseBody ?? []);

            Log.Information("Response {Id} body rewritten: {Before} bytes became {After} bytes.",
                exchange.Id, originalBody?.Length ?? 0, exchange.ResponseBody?.Length ?? 0);
        }

        ExchangeCompleted?.Invoke(exchange);
    }

    /// <summary>Stops recording when an armed window's condition matches.</summary>
    private void CheckCaptureWindow(Exchange exchange)
    {
        if (Volatile.Read(ref _captureWindow) is not { } window) return;

        bool closes;

        try
        {
            closes = window.StopWhen(exchange);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The armed capture window threw while testing {Method} {Url}. "
                + "Recording continues.", exchange.Method, exchange.Url);
            return;
        }

        if (!closes) return;

        // Claimed atomically. Requests arrive on many threads at once, so two of them can match the
        // same window simultaneously; without this both would stop recording and both would log it,
        // and the count each reported would be wrong. Only the thread that swaps the window out
        // carries on.
        if (Interlocked.CompareExchange(ref _captureWindow, null, window) != window) return;

        _captureEnabled = false;

        Log.Information(
            "Capture window armed by {Actor} closed on {Method} {Url} (exchange {Id}): "
            + "{Description}. Recording is now off with {Count} exchange(s) held.",
            window.ArmedBy ?? "unidentified", exchange.Method, exchange.Url, exchange.Id,
            window.Description, Capture.Count);
    }

    /// <summary>Copies Titanium's header collection into our own plain list.</summary>
    private static List<(string Name, string Value)> ReadHeaders(IEnumerable<HttpHeader> headers)
    {
        var result = new List<(string Name, string Value)>();
        foreach (var header in headers)
            result.Add((header.Name, header.Value));
        return result;
    }

    /// <summary>
    /// Bodies past this are stored cut down to it, with the truncated flag set. A megabyte
    /// comfortably holds any API payload a person actually reads.
    /// </summary>
    private const int MaxBodyBytes = 1024 * 1024;

    /// <summary>Content types whose bodies are worth keeping, matched as a prefix.</summary>
    private static readonly string[] CapturedContentTypes =
    [
        "text/",
        "application/json",
        "application/ld+json",
        "application/problem+json",
        "application/xml",
        "application/xhtml+xml",
        "application/javascript",
        "application/x-javascript",
        "application/x-www-form-urlencoded",
        "application/graphql",
        "application/csp-report",
        "multipart/form-data",
    ];

    /// <summary>
    /// True when a body with this content type and declared length is worth storing.
    /// </summary>
    /// <remarks>
    /// Reading a body means buffering it in memory and, for a streaming response, waiting for an
    /// end that may never arrive. So this leans toward the things someone reverse-engineers an API
    /// with — JSON, forms, text — and skips bulk media that would cost memory without ever being
    /// read.
    /// </remarks>
    /// <param name="contentType">Raw Content-Type header value, or null when absent.</param>
    /// <param name="contentLength">Declared length, or -1 when the body is chunked.</param>
    private static bool ShouldCaptureExchange(string? contentType, long contentLength)
    {
        // Server-sent events never end on their own. Reading one would hang the exchange.
        if (contentType is not null
            && contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return false;

        // A body with no content type is usually small and hand-rolled, which makes it exactly the
        // kind of thing someone is trying to understand. Keep it.
        if (string.IsNullOrWhiteSpace(contentType))
            return contentLength is > 0 and <= MaxBodyBytes;

        var bareType = contentType.Split(';')[0].Trim();

        if (!CapturedContentTypes.Any(
                candidate => bareType.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            return false;

        // A chunked body of an interesting type is worth reading even though its size is not known
        // up front — that is how most JSON APIs stream their responses.
        return contentLength <= MaxBodyBytes;
    }

    /// <summary>Cuts a body down to the cap. Returns the bytes to store and whether they were cut.</summary>
    private static (byte[] Body, bool WasTruncated) Truncate(byte[] body) =>
        body.Length <= MaxBodyBytes ? (body, false) : (body[..MaxBodyBytes], true);

    // ---------------------------------------------------------------------------------
    // The marker file recording that we own the machine's proxy settings
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Reads the machine's current proxy settings, so we can put them back later. Nulls mean the
    /// value was not set, which is itself worth recording: restoring then means deleting it, not
    /// writing a zero.
    /// </summary>
    private static (int? Enable, string? Server) ReadSystemProxySettings()
    {
        if (!OperatingSystem.IsWindows()) return (null, null);

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
            if (key is null) return (null, null);

            return (key.GetValue("ProxyEnable") as int?, key.GetValue("ProxyServer") as string);
        }
        catch (Exception ex)
        {
            Log.Warning("Could not read the current system proxy settings: {Reason}", ex.Message);
            return (null, null);
        }
    }

    /// <summary>Writes the recorded settings back. Returns false when there is nothing to write to.</summary>
    private static bool RestoreSystemProxySettings((int? Enable, string? Server) original)
    {
        if (!OperatingSystem.IsWindows()) return false;

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
        if (key is null) return false;

        // A value that was absent before is deleted rather than written as zero or empty, so the
        // machine ends up in the state it was actually in, not an equivalent-looking one.
        if (original.Enable is { } enable)
            key.SetValue("ProxyEnable", enable, Microsoft.Win32.RegistryValueKind.DWord);
        else
            key.DeleteValue("ProxyEnable", throwOnMissingValue: false);

        if (original.Server is { } server)
            key.SetValue("ProxyServer", server, Microsoft.Win32.RegistryValueKind.String);
        else
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);

        NotifySystemProxyChanged();
        return true;
    }

    /// <summary>
    /// Tells Windows the proxy settings changed. Without it the registry is correct but every
    /// already-running application carries on using the values it read at startup, so the user sees
    /// no change and reasonably concludes the repair did not work.
    /// </summary>
    private static void NotifySystemProxyChanged()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            Internet.InternetSetOption(IntPtr.Zero, Internet.OptionSettingsChanged, IntPtr.Zero, 0);
            Internet.InternetSetOption(IntPtr.Zero, Internet.OptionRefresh, IntPtr.Zero, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing to notify. The registry write already happened, which is the part that lasts,
            // and this must never be the reason a restore is reported as having failed.
        }
    }

    /// <summary>Records that this process has taken over the system proxy.</summary>
    private void RecordProxyState((int? Enable, string? Server) original)
    {
        try
        {
            var marker = new ProxyStateMarker(
                System.Environment.ProcessId, DateTimeOffset.Now, Port, original.Enable, original.Server);

            File.WriteAllText(DirectoryManager.ProxyStateFilePath, JsonSerializer.Serialize(marker));
        }
        catch (IOException ex)
        {
            // Failing to write the marker is not worth refusing to start over. It costs only the
            // automatic cleanup on the next run, not anything about this one.
            Log.Warning("Could not record proxy state: {Reason}", ex.Message);
        }
    }

    /// <summary>Clears the marker, once the system proxy has actually been restored.</summary>
    private static void ClearProxyState()
    {
        try
        {
            if (File.Exists(DirectoryManager.ProxyStateFilePath))
                File.Delete(DirectoryManager.ProxyStateFilePath);
        }
        catch (IOException)
        {
            // A leftover file is harmless: the next startup checks whether the process is alive
            // before acting on it.
        }
    }

    /// <summary>
    /// Returns the marker left by a run that is no longer alive, or null when there is nothing
    /// stale to undo.
    /// </summary>
    private static ProxyStateMarker? FindStaleProxyState()
    {
        try
        {
            if (!File.Exists(DirectoryManager.ProxyStateFilePath)) return null;

            var marker = JsonSerializer.Deserialize<ProxyStateMarker>(
                File.ReadAllText(DirectoryManager.ProxyStateFilePath));

            if (marker is null) return null;

            // Our own marker is not stale. This matters when a second ReqTree starts while the
            // first is still running.
            if (marker.ProcessId == System.Environment.ProcessId) return null;

            return IsProcessRunning(marker.ProcessId) ? null : marker;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // An unreadable marker tells us nothing, and guessing would risk undoing a proxy
            // setting ReqTree never made.
            return null;
        }
    }

    /// <summary>True when a process with this id is still alive.</summary>
    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process — exactly the case this method exists to detect.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
