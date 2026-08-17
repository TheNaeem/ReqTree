namespace ReqTree.Proxy.Objects;

/// <summary>Which point in an exchange something runs at.</summary>
public enum ProxyHook
{
    /// <summary>
    /// The request has been read but not sent upstream. Changes made here still reach the server,
    /// and setting the response half means the request never leaves the machine.
    /// </summary>
    BeforeRequest,

    /// <summary>
    /// The response has arrived but not reached the client. Changes made here are what the client
    /// ends up seeing.
    /// </summary>
    BeforeResponse,
}
