namespace UdpHolePunching.Server;

public class Message
{
    public string Type { get; set; } = ""; // REGISTER, GET_PEER, PEER_INFO, PUNCH_NOTIFY, etc.
    public string PeerName { get; set; } = "";
    public string TargetPeer { get; set; } = "";
    public string PeerIp { get; set; } = "";
    public int PeerPort { get; set; }
    public string Data { get; set; } = "";
}