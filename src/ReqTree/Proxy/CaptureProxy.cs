using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using ReqTree.App;
using ReqTree.Proxy.Objects;
using Serilog;
using Serilog.Extensions.Logging;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;

namespace ReqTree.Proxy;

/// <summary>
/// Owns the Titanium.Web.Proxy lifecycle: root certificate, listening endpoint, and system proxy
/// registration.
/// </summary>
/// <remarks>
/// The Titanium server is built fresh in <see cref="TryStart"/> and disposed in <see cref="Stop"/>
/// rather than living for the lifetime of this object. That is what makes the proxy genuinely
/// restartable: a stopped-and-restarted ProxyServer would be asked to add an endpoint it already
/// has. Callers never see the swap, because the port and the state live on this class, not on the
/// server underneath.
///
/// Capture transport, file-capture names, script runners, and Windows proxy ownership each keep
/// their own coupled state behind an internal module. This class remains the stable façade the MCP
/// layer uses to start, stop, and configure all of them.
/// </remarks>
public sealed class CaptureProxy : IAsyncDisposable
{
    private readonly bool _registerAsSystemProxy;
    private readonly bool _installCertificateTrust;
    private readonly SystemProxyLease _systemProxy = new();

    /// <summary>Serialises start and stop, which can arrive concurrently once tools can call them.</summary>
    private readonly Lock _lifecycleLock = new();

    /// <summary>Null whenever the proxy is stopped.</summary>
    private ProxyServer? _proxyServer;
    private readonly CapturePipeline _pipeline;
    /// <summary>
    /// Everything this proxy has captured. The same shape a capture loaded from a file has, which
    /// is what the read side gets handed instead when there is no proxy running at all.
    /// </summary>
    public ExchangeStore Capture { get; }

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
        get => _pipeline.CaptureEnabled;
        set => _pipeline.CaptureEnabled = value;
    }

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

    private readonly CaptureCatalog _captures;

    /// <summary>Names of the captures opened from files.</summary>
    public IReadOnlyList<string> OpenedCaptures
        => _captures.OpenedNames;

    /// <summary>Adds an opened capture under a name, if that name is not already in use.</summary>
    public bool AddOpenedCapture(string name, ExchangeStore store) => _captures.AddOpened(name, store);

    /// <summary>Forgets an opened capture. The live one cannot be closed.</summary>
    public bool CloseCapture(string name) => _captures.Close(name);

    /// <summary>
    /// The capture a read tool means. Null or "live" is the one being recorded; anything else is
    /// a file opened under that name.
    /// </summary>
    public ExchangeStore? ResolveCapture(string? name) => _captures.Resolve(name);

    internal CaptureName ResolveOpenedCaptureName(string fullPath, string? requestedName) =>
        _captures.ResolveOpenedName(fullPath, requestedName);

    /// <summary>
    /// Raised once an exchange is finished, whether answered upstream or by a rule. The console
    /// view is the only thing that listens today.
    /// </summary>
    public event Action<Exchange>? ExchangeCompleted;

    /// <summary>An armed stop condition, and what it was described as.</summary>
    public sealed record CaptureWindow(Func<Exchange, bool> StopWhen, string Description, string? ArmedBy);

    /// <summary>What is armed right now, or null.</summary>
    public CaptureWindow? ArmedWindow => _pipeline.ArmedWindow is { } window
        ? new CaptureWindow(window.StopWhen, window.Description, window.ArmedBy)
        : null;

    /// <summary>Arms a stop condition, replacing any already armed.</summary>
    public void ArmCaptureWindow(CaptureWindow window)
    {
        _pipeline.Arm(new CaptureWindowState(window.StopWhen, window.Description, window.ArmedBy));
    }

    /// <summary>Disarms the window. Returns what was armed, or null if nothing was.</summary>
    public CaptureWindow? DisarmCaptureWindow() => _pipeline.Disarm() is { } window
        ? new CaptureWindow(window.StopWhen, window.Description, window.ArmedBy)
        : null;

    /// <summary>Port this proxy listens on.</summary>
    public int Port { get; }

    /// <summary>True while the proxy is listening.</summary>
    public bool IsRunning
    {
        get { lock (_lifecycleLock) return _proxyServer?.ProxyRunning ?? false; }
    }

    /// <summary>True when we have pointed the machine's proxy settings at ourselves.</summary>
    public bool IsSystemProxy => _systemProxy.IsTaken;

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
        _captures = new CaptureCatalog(Capture);
        _pipeline = new CapturePipeline(
            Capture,
            ApplyBehaviour,
            exchange => ExchangeCompleted?.Invoke(exchange));

        // A cap that stops recording has to turn capture off as well, or the hooks carry on
        // building exchanges the store then refuses one at a time.
        Capture.LimitReached += reason =>
        {
            _pipeline.CaptureEnabled = false;
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
                    if (_systemProxy.TakeOver(server, endPoint, Port)
                        is SystemProxyTakeover.OwnedByAnotherReqTree)
                    {
                        Log.Warning(
                            "Another ReqTree already owns this machine's proxy settings, so this one "
                            + "is listening on port {Port} without touching them. Point a client "
                            + "here yourself, or stop the other one first. Taking over would record "
                            + "ITS port as the setting to restore, and whichever of us stopped last "
                            + "would leave the machine pointed at a dead port.", Port);
                    }
                }

                _proxyServer = server;

                Log.Information("Proxy listening on port {Port}. System proxy {SystemProxy}.",
                    Port, _systemProxy.IsTaken ? "points at ReqTree" : "was not changed");

                return true;
            }
            catch (Exception ex)
            {
                // Two separate attempts, not one. Sharing a try meant a registry failure skipped
                // the teardown below it — leaving a half-started proxy still holding its listener
                // and never disposed, on top of the settings being wrong. Each has to be able to
                // fail without taking the other with it.
                if (_systemProxy.IsTaken)
                {
                    try
                    {
                        _systemProxy.RestoreAfterFailedStart();
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

                // Unconditional: the claim may have been taken and the takeover then failed before
                // the lease recorded its takeover, and holding it after a failed start would lock
                // every later run of this process out of the settings it never touched.
                _systemProxy.ReleaseOwnership();

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
            var hadSystemProxy = _systemProxy.IsTaken;

            if (_systemProxy.IsTaken)
            {
                try
                {
                    // Writes back exactly what was there before we started, rather than blanket-
                    // disabling the proxy and clobbering a real corporate setting. This is the same
                    // code the crash-recovery path uses; Titanium's own RestoreOriginalProxySettings
                    // is deliberately not used, because it reports success even when it restores
                    // nothing, and one mechanism that is known to work beats two that might.
                    //
                    // It returns false rather than throwing when the registry key cannot be opened
                    // for writing. That has to be treated exactly like a thrown failure: the
                    // settings are still ours, the marker must survive for the next run, and the
                    // message below must not claim a restore that did not happen.
                    if (!_systemProxy.TryRestore())
                    {
                        Log.Error(
                            "Could not restore the machine's proxy settings. They still point at "
                            + "ReqTree on port {Port}, so applications will lose connectivity when "
                            + "this process exits. Starting ReqTree again will repair it, or set "
                            + "them back by hand in Internet Options.", Port);
                    }
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

            // Given up whether or not the restore worked. If it failed the marker is still there and
            // the next run repairs it, and that run needs to be able to claim ownership to do so.
            _systemProxy.ReleaseOwnership();

            var wasRunning = server.ProxyRunning;
            if (wasRunning) server.Stop();

            // Disposed rather than kept, so the next start builds a clean server instead of reusing
            // one that already holds an endpoint.
            server.Dispose();
            _proxyServer = null;

            // Reported from what actually happened, not from what was attempted. The lease clears
            // its taken state only after a successful restore, so saying "restored" after a failure
            // would repeat the false-success claim that made Titanium's own method dangerous.
            if (!hadSystemProxy)
                Log.Information("Proxy stopped. System proxy settings were never changed.");
            else if (!_systemProxy.IsTaken)
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
        => _systemProxy.CleanStaleState();

    public ValueTask DisposeAsync()
    {
        Stop();

        // Stop releases it on the path where a proxy was running. This catches the other one: a
        // process that claimed ownership, failed somewhere odd, and is now going away holding it.
        _systemProxy.Dispose();

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
                var scriptResult = ScriptRuntime.Run(script, exchange);
                if (scriptResult is not ScriptRunResult.Completed)
                {
                    // Disabled, not merely skipped. A non-cooperative runner cannot be stopped, so
                    // a finite runner cap prevents abandoned threads growing without bound.
                    script.Enabled = false;
                    script.RecordTimeout();

                    if (scriptResult is ScriptRunResult.RunnerLimitReached)
                        Log.Error(
                            "Script {Script} (added by {Actor}) could not get one of the {Limit} "
                            + "bounded timed-script runners on {Hook} for {Method} (exchange {Id}), "
                            + "so it has been DISABLED and the request carried on without it.",
                            script.Name, script.AddedBy ?? "unidentified", ScriptRuntime.MaxTimedRunners,
                            hook, exchange.Method, exchange.Id);
                    else
                        Log.Error(
                            "Script {Script} (added by {Actor}) did not finish within {Timeout} on "
                            + "{Hook} for {Method} {Url} (exchange {Id}), so it has been DISABLED and "
                            + "the request carried on without it. The abandoned runner cannot be "
                            + "stopped and will use CPU until it returns on its own. Inspect the "
                            + "script for an infinite loop, blocking wait, or async operation that "
                            + "never completes; list_scripts shows the source.",
                            script.Name, script.AddedBy ?? "unidentified", script.Timeout,
                            hook, exchange.Method, exchange.Url, exchange.Id);

                    continue;
                }

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

        _pipeline.Attach(server);
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

}
