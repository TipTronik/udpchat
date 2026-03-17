using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Net.NetworkInformation;

namespace UdpHolePunching.Pure;

// Типы NAT
public enum NatType
{
    Unknown,
    FullCone,
    RestrictedCone,
    PortRestrictedCone,
    Symmetric
}

// Состояние соединения
public enum ConnectionState
{
    Disconnected,
    Discovering,
    UpnpAttempt,
    PmpAttempt,
    HolePunching,
    PortScanning,
    TurnRelay,
    Connected,
    Failed
}

// Информация о пире
public class PeerInfo
{
    public required string PeerName { get; set; }
    public IPEndPoint? ReportedEndpoint { get; set; }
    public IPEndPoint? DirectEndpoint { get; set; }
    public IPEndPoint? TurnEndpoint { get; set; }
    public List<IPEndPoint> CandidateEndpoints { get; set; } = new();
    public NatType NatType { get; set; } = NatType.Unknown;
    public ConnectionState State { get; set; } = ConnectionState.Disconnected;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public int PunchAttempts { get; set; }
    public bool UseTurn { get; set; }
}

// Сообщение для обмена
public class PeerMessage
{
    public required string Type { get; set; } // REGISTER, GET_PEER, PEER_INFO, PUNCH, DATA
    public required string Sender { get; set; }
    public string? Target { get; set; }
    public string? Data { get; set; }
    //public IPEndPoint? ExternalEndpoint { get; set; }
    //public string Ip { get; set; }
    //public int Port { get; set; }
    public string? ExternalEndpoint { get; set; }
    public string? InternalEndpoint { get; set; }
    public NatType? NatType { get; set; }
    public int Sequence { get; set; }
}