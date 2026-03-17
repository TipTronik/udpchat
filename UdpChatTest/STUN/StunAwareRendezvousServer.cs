using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UdpHolePunching.Client;
using UdpHolePunching.STUN;

public class StunAwareRendezvousServer
{
    private readonly UdpClient _server;
    private readonly Dictionary<string, ClientInfo> _clients = new();
    private readonly object _lock = new();

    public StunAwareRendezvousServer(int port = 5555)
    {
        _server = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        Console.WriteLine($"[Server] STUN-aware server started on port {port}");
    }

    public async Task StartAsync()
    {
        while (true)
        {
            try
            {
                var result = await _server.ReceiveAsync();
                _ = ProcessMessageAsync(result.Buffer, result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] Error: {ex.Message}");
            }
        }
    }

    private async Task ProcessMessageAsync(byte[] data, IPEndPoint sender)
    {
        var json = Encoding.UTF8.GetString(data);
        var baseMsg = JsonSerializer.Deserialize<BaseMessage>(json);
        
        if (baseMsg == null) return;

        Console.WriteLine($"[Server] {baseMsg.Type} from {sender}");

        switch (baseMsg.Type)
        {
            case "REGISTER":
                var regMsg = JsonSerializer.Deserialize<RegistrationMessage>(json)!;
                await HandleRegisterAsync(regMsg, sender);
                break;
                
            case "GET_PEER":
                var queryMsg = JsonSerializer.Deserialize<PeerQueryMessage>(json)!;
                await HandleGetPeerAsync(queryMsg, sender);
                break;
        }
    }

    private async Task HandleRegisterAsync(RegistrationMessage msg, IPEndPoint sender)
    {
        lock (_lock)
        {
            _clients[msg.PeerName] = new ClientInfo
            {
                PeerName = msg.PeerName,
                ReportedEndpoint = sender,
                PublicEndpoint = msg.PublicEndpoint != null ? IPEndPoint.Parse(msg.PublicEndpoint) : null,
                NatType = msg.NatType,
                LastSeen = DateTime.UtcNow
            };
        }

        Console.WriteLine($"[Server] Registered {msg.PeerName} - " +
                         $"Public: {msg.PublicEndpoint}, NAT: {msg.NatType}");

        var response = new BaseMessage { Type = "REGISTERED" };
        await SendAsync(response, sender);
    }

    private async Task HandleGetPeerAsync(PeerQueryMessage msg, IPEndPoint sender)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(msg.TargetPeer, out var target))
            {
                Console.WriteLine($"[Server] Sending {msg.TargetPeer} info to {msg.Sender}");

                var response = new PeerInfoMessage
                {
                    Type = "PEER_INFO",
                    PeerName = target.PeerName,
                    ReportedEndpoint = target.ReportedEndpoint,
                    PublicEndpoint = target.PublicEndpoint,
                    NatType = target.NatType
                };
                SendAsync(response, sender);

                // Уведомляем целевого пира
                var notify = new PeerWantsConnectMessage
                {
                    Type = "PEER_WANTS_CONNECT",
                    SenderName = msg.Sender,
                    SenderIp = sender.Address.ToString(),
                    SenderPort = sender.Port,
                    SenderPublicEndpoint = msg.SenderPublicEndpoint,
                    SenderNatType = msg.SenderNatType
                };
                SendAsync(notify, target.ReportedEndpoint);
            }
            else
            {
                var error = new BaseMessage
                {
                    Type = "ERROR",
                    Data = $"Peer {msg.TargetPeer} not found"
                };
                SendAsync(error, sender);
            }
        }
    }

    private async Task SendAsync(object message, IPEndPoint target)
    {
        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);
        await _server.SendAsync(data, data.Length, target);
    }

    private class ClientInfo
    {
        public required string PeerName { get; set; }
        public required IPEndPoint ReportedEndpoint { get; set; }
        public IPEndPoint? PublicEndpoint { get; set; }
        public NatType NatType { get; set; }
        public DateTime LastSeen { get; set; }
    }
}