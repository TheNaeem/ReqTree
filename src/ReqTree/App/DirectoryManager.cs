namespace ReqTree.App;

/// <summary>
/// Every file ReqTree writes lives under one folder, resolved here and nowhere else.
/// Keeping this in one place means "where does my data live?" has exactly one answer.
/// </summary>
public static class DirectoryManager
{
    /// <summary>
    /// Creates the data folders the first time anything asks for a path.
    /// </summary>
    /// <remarks>
    /// The runtime guarantees this runs exactly once, before any member below is read, so no
    /// caller ever has to remember to prepare the directory first. Worth knowing when reading a
    /// stack trace: a failure here surfaces as a TypeInitializationException naming this class,
    /// with the real cause (a permissions problem, say) as the inner exception.
    /// </remarks>
    static DirectoryManager()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <summary>Root data folder, e.g. C:\Users\me\AppData\Local\ReqTree.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReqTree");

    /// <summary>The MITM root certificate we generate once and reuse across runs.</summary>
    public static string RootCertificatePath => Path.Combine(DataDirectory, "reqtree-root.pfx");

    /// <summary>
    /// Public half of the root certificate, in the format curl, phones and other devices expect.
    /// Exported on every run so it is always there when a user needs to trust ReqTree somewhere
    /// other than this machine's own certificate store.
    /// </summary>
    public static string RootCertificateCerPath => Path.Combine(DataDirectory, "reqtree-root.cer");

    // There is deliberately no path for a capture database here. Captures live in memory and are
    // written only where a caller asks for them, so a fixed "the database" location would be a
    // standing invitation to build the live-writing design this one exists to avoid.

    /// <summary>
    /// Rolling log files. Note what does *not* go here: captured traffic. These files are plain
    /// text, kept for days, and never cleaned up by the user, so anything written to them is a
    /// credential sitting on disk. Logs describe what ReqTree did, never what it intercepted.
    /// </summary>
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Records that this process changed the machine's proxy settings, so a later run can notice
    /// a crashed predecessor and undo the damage.
    /// </summary>
    public static string ProxyStateFilePath => Path.Combine(DataDirectory, "proxy-state.json");
}
