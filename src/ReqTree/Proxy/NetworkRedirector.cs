using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using Divert.Windows;
using Serilog;

namespace ReqTree.Proxy;

// This type is constructed only behind CaptureProxy's IsWindowsVersionAtLeast guard. Keeping the
// platform boundary there lets the rest of CaptureProxy remain cross-platform while this file can
// use WinDivert throughout without repeating the same annotation on every private helper.
#pragma warning disable CA1416

/// <summary>
/// Redirects local IPv4 TCP/80 and TCP/443 traffic into Titanium's transparent endpoint.
/// </summary>
/// <remarks>
/// WinDivert is the WFP-backed packet layer. The application still believes it is connected to
/// the origin: request packets are rewritten to the local transparent listener, and response
/// packets have the original origin restored as their source before re-entering the TCP stack.
/// Titanium then does the HTTP/TLS work exactly as it does for explicitly proxied traffic.
///
/// The proxy's own origin connections must not be redirected or they recurse back into ReqTree.
/// WinDivert's network layer has no process-id field, so the Windows TCP owner table identifies
/// and remembers ReqTree's ephemeral source ports.
/// </remarks>
internal sealed class NetworkRedirector : IDisposable
{
    private const int MaxPacketBytes = 65_535;
    private const int MaxMappings = 65_536;

    private readonly int _transparentPort;
    private readonly int _processId = Environment.ProcessId;
    private readonly ConcurrentDictionary<ClientEndpoint, OriginEndpoint> _origins = new();
    private readonly ConcurrentQueue<ClientEndpoint> _originOrder = new();
    private readonly ConcurrentDictionary<ushort, byte> _proxyPorts = new();
    private readonly CancellationTokenSource _stop = new();

    private DivertService? _requests;
    private DivertService? _responses;
    private Task? _requestLoop;
    private Task? _responseLoop;
    private volatile bool _running;
    private int _disposed;

    internal NetworkRedirector(int transparentPort)
    {
        _transparentPort = transparentPort;
    }

    internal bool IsRunning => _running;

    internal void Start()
    {
        if (IsRunning) return;

        try
        {
            // IPv4 is deliberate for the first network-capture path. Rewriting IPv6 safely also
            // requires walking extension headers and a second transparent listener; passing it
            // untouched is safer than guessing at offsets and corrupting unrelated traffic.
            _requests = new DivertService(
                "outbound and ip and tcp and !loopback and "
                + "(tcp.DstPort == 80 or tcp.DstPort == 443)",
                DivertLayer.Network,
                priority: 100,
                DivertFlags.None,
                runContinuationsAsynchronously: true);

            _responses = new DivertService(
                $"outbound and ip and tcp.SrcPort == {_transparentPort}",
                DivertLayer.Network,
                priority: 101,
                DivertFlags.None,
                runContinuationsAsynchronously: true);

            _running = true;
            _requestLoop = Task.Run(() => RedirectRequestsAsync(_stop.Token));
            _responseLoop = Task.Run(() => RestoreResponsesAsync(_stop.Token));

            Log.Information(
                "Network capture active through WinDivert: local IPv4 TCP ports 80 and 443 are "
                + "redirected to transparent port {Port}.", _transparentPort);
        }
        catch (Exception ex)
        {
            Dispose();
            throw new InvalidOperationException(
                "Could not start network capture. WinDivert requires ReqTree to run as "
                + "administrator so its WFP driver can open a redirect handle.", ex);
        }
    }

    private async Task RedirectRequestsAsync(CancellationToken cancellationToken)
    {
        var packets = new byte[MaxPacketBytes];
        var addresses = new DivertAddress[1];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _requests!.ReceiveAsync(packets, addresses, cancellationToken);
                if (result.DataLength <= 0 || result.AddressLength <= 0) continue;

                var packet = packets.AsSpan(0, result.DataLength);
                if (!TryReadIpv4Tcp(packet, out var parsed))
                {
                    await SendAsync(_requests, packets, result.DataLength, addresses, cancellationToken);
                    continue;
                }

                var client = new ClientEndpoint(parsed.SourceAddress, parsed.SourcePort);
                if (IsProxyConnection(parsed.SourcePort))
                {
                    if (parsed.IsFinished) _proxyPorts.TryRemove(parsed.SourcePort, out _);
                    await SendAsync(_requests, packets, result.DataLength, addresses, cancellationToken);
                    continue;
                }

                Remember(client, new OriginEndpoint(parsed.DestinationAddress, parsed.DestinationPort));

                // Deliver the packet as though it arrived from the network. Using the machine's
                // actual source address (not 127.0.0.1) is required for WinDivert loopback routing.
                WriteIpv4Address(packet, 16, parsed.SourceAddress);
                BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(parsed.TcpOffset + 2, 2),
                    checked((ushort)_transparentPort));

                var address = addresses[0];
                address.IsOutbound = false;
                DivertHelper.CalculateChecksums(packet, ref address, DivertHelperFlags.None);
                addresses[0] = address;

                await SendAsync(_responses!, packets, result.DataLength, addresses, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Network-capture request redirection stopped unexpectedly.");
            CloseHandles();
        }
    }

    private async Task RestoreResponsesAsync(CancellationToken cancellationToken)
    {
        var packets = new byte[MaxPacketBytes];
        var addresses = new DivertAddress[1];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _responses!.ReceiveAsync(packets, addresses, cancellationToken);
                if (result.DataLength <= 0 || result.AddressLength <= 0) continue;

                var packet = packets.AsSpan(0, result.DataLength);
                if (!TryReadIpv4Tcp(packet, out var parsed)
                    || !_origins.TryGetValue(
                        new ClientEndpoint(parsed.DestinationAddress, parsed.DestinationPort),
                        out var origin))
                {
                    await SendAsync(_responses, packets, result.DataLength, addresses, cancellationToken);
                    continue;
                }

                WriteIpv4Address(packet, 12, origin.Address);
                BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(parsed.TcpOffset, 2), origin.Port);

                var address = addresses[0];
                DivertHelper.CalculateChecksums(packet, ref address, DivertHelperFlags.None);
                addresses[0] = address;

                await SendAsync(_responses, packets, result.DataLength, addresses, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Network-capture response redirection stopped unexpectedly.");
            CloseHandles();
        }
    }

    private bool IsProxyConnection(ushort sourcePort)
    {
        if (_proxyPorts.ContainsKey(sourcePort)) return true;
        if (!TcpOwnerTable.IsOwnedBy(sourcePort, _processId)) return false;

        _proxyPorts.TryAdd(sourcePort, 0);
        return true;
    }

    private void Remember(ClientEndpoint client, OriginEndpoint origin)
    {
        _origins[client] = origin;
        _originOrder.Enqueue(client);

        while (_origins.Count > MaxMappings && _originOrder.TryDequeue(out var oldest))
            _origins.TryRemove(oldest, out _);
    }

    private static ValueTask<int> SendAsync(
        DivertService service,
        byte[] packet,
        int packetLength,
        DivertAddress[] addresses,
        CancellationToken cancellationToken) =>
        service.SendAsync(packet.AsMemory(0, packetLength), addresses.AsMemory(0, 1), cancellationToken);

    private static bool TryReadIpv4Tcp(ReadOnlySpan<byte> packet, out ParsedPacket parsed)
    {
        parsed = default;
        if (packet.Length < 40 || (packet[0] >> 4) != 4 || packet[9] != 6) return false;

        var tcpOffset = (packet[0] & 0x0f) * 4;
        if (tcpOffset < 20 || packet.Length < tcpOffset + 20) return false;

        parsed = new ParsedPacket(
            BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(12, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16, 4)),
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(tcpOffset, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(tcpOffset + 2, 2)),
            tcpOffset,
            (packet[tcpOffset + 13] & 0x05) != 0);
        return true;
    }

    private static void WriteIpv4Address(Span<byte> packet, int offset, uint address) =>
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(offset, 4), address);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _stop.Cancel();
        CloseHandles();

        try
        {
            Task.WaitAll(
                [_requestLoop ?? Task.CompletedTask, _responseLoop ?? Task.CompletedTask],
                TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.All(inner => inner is OperationCanceledException or ObjectDisposedException))
        {
        }

        _stop.Dispose();
    }

    /// <summary>
    /// Closes both packet handles together. A failure in either loop must fail open: leaving only
    /// one side active would keep diverting connections without being able to complete them.
    /// </summary>
    private void CloseHandles()
    {
        _running = false;
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A loop can observe its handle closing just after Dispose has finished. The handles
            // below are already null then; there is nothing left to cancel.
        }

        Interlocked.Exchange(ref _requests, null)?.Dispose();
        Interlocked.Exchange(ref _responses, null)?.Dispose();
    }

    private readonly record struct ClientEndpoint(uint Address, ushort Port);
    private readonly record struct OriginEndpoint(uint Address, ushort Port);
    private readonly record struct ParsedPacket(
        uint SourceAddress,
        uint DestinationAddress,
        ushort SourcePort,
        ushort DestinationPort,
        int TcpOffset,
        bool IsFinished);

    private static class TcpOwnerTable
    {
        private const int AddressFamilyIpv4 = 2;
        private const int OwnerPidConnections = 4;
        private const int InsufficientBuffer = 122;
        private const int RowBytes = 24;

        internal static bool IsOwnedBy(ushort localPort, int processId)
        {
            var size = 0;
            var first = GetExtendedTcpTable(
                IntPtr.Zero, ref size, sort: false, AddressFamilyIpv4, OwnerPidConnections, 0);
            if (first != InsufficientBuffer || size <= sizeof(uint)) return false;

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(
                    buffer, ref size, sort: false, AddressFamilyIpv4, OwnerPidConnections, 0);
                if (result != 0) return false;

                var count = Marshal.ReadInt32(buffer);
                var row = IntPtr.Add(buffer, sizeof(uint));
                for (var i = 0; i < count; i++, row = IntPtr.Add(row, RowBytes))
                {
                    var port = unchecked((ushort)IPAddress.NetworkToHostOrder(
                        unchecked((short)(uint)Marshal.ReadInt32(row, 8))));
                    var owner = Marshal.ReadInt32(row, 20);
                    if (port == localPort && owner == processId) return true;
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(
            IntPtr table,
            ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool sort,
            int addressFamily,
            int tableClass,
            uint reserved);
    }
}
#pragma warning restore CA1416
