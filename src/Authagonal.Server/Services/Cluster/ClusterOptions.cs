namespace Authagonal.Server.Services.Cluster;

public sealed class ClusterOptions
{
    public bool Enabled { get; set; } = true;
    public string MulticastGroup { get; set; } = "239.42.42.42";
    public int MulticastPort { get; set; } = 19847;
    public string? InternalUrl { get; set; }
    public string? Secret { get; set; }
    public int GossipIntervalSeconds { get; set; } = 5;
    public int DiscoveryIntervalSeconds { get; set; } = 10;
    public int PeerStaleAfterSeconds { get; set; } = 30;
}
