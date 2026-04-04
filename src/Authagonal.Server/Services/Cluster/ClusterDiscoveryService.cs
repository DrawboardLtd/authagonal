using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Authagonal.Server.Services.Cluster;

public sealed class ClusterDiscoveryService(
    PeerRegistry peerRegistry,
    ClusterNode clusterNode,
    IOptions<ClusterOptions> options,
    ILogger<ClusterDiscoveryService> logger) : BackgroundService
{
    private UdpClient? _udpClient;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        var multicastAddress = IPAddress.Parse(opts.MulticastGroup);

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, opts.MulticastPort));
            _udpClient.JoinMulticastGroup(multicastAddress);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to join multicast group {Group}:{Port} — cluster discovery disabled, falling back to InternalUrl only",
                opts.MulticastGroup, opts.MulticastPort);
            return;
        }

        logger.LogInformation("Cluster discovery started on {Group}:{Port}, nodeId={NodeId}",
            opts.MulticastGroup, opts.MulticastPort, clusterNode.NodeId);

        // Start listener and announcer in parallel
        var listenTask = ListenAsync(stoppingToken);
        var announceTask = AnnounceAsync(multicastAddress, stoppingToken);

        await Task.WhenAny(listenTask, announceTask);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient!.ReceiveAsync(ct);
                var message = Encoding.UTF8.GetString(result.Buffer);
                var parts = message.Split('|', 2);

                if (parts.Length != 2)
                    continue;

                var nodeId = parts[0];
                var address = parts[1];

                // Ignore self
                if (string.Equals(nodeId, clusterNode.NodeId, StringComparison.OrdinalIgnoreCase))
                    continue;

                peerRegistry.AddOrRefresh(nodeId, address);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error receiving discovery message");
            }
        }
    }

    private async Task AnnounceAsync(IPAddress multicastAddress, CancellationToken ct)
    {
        var opts = options.Value;
        var endpoint = new IPEndPoint(multicastAddress, opts.MulticastPort);

        // Determine our own HTTP address
        var httpAddress = GetOwnHttpAddress();
        var announcement = Encoding.UTF8.GetBytes($"{clusterNode.NodeId}|{httpAddress}");

        // Initial announcement
        try
        {
            await _udpClient!.SendAsync(announcement, announcement.Length, endpoint);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to send initial discovery announcement");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(opts.DiscoveryIntervalSeconds));

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                // Prune stale peers
                peerRegistry.Prune(TimeSpan.FromSeconds(opts.PeerStaleAfterSeconds));

                await _udpClient!.SendAsync(announcement, announcement.Length, endpoint);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to send discovery announcement");
            }
        }
    }

    private string GetOwnHttpAddress()
    {
        // Use InternalUrl if configured (operator knows best)
        var internalUrl = options.Value.InternalUrl;

        // Try to determine the IP address of this instance
        // In container environments, this will typically be the pod IP
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var ip = host.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is not null)
                return $"http://{ip}:8080";
        }
        catch
        {
            // ignored
        }

        return "http://localhost:8080";
    }

    public override void Dispose()
    {
        try
        {
            _udpClient?.DropMulticastGroup(IPAddress.Parse(options.Value.MulticastGroup));
        }
        catch
        {
            // ignored
        }

        _udpClient?.Dispose();
        base.Dispose();
    }
}
