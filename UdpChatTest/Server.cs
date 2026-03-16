using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UdpHolePunching.Server;

public class RendezvousServer
{
    private readonly UdpClient _udpServer;
    private readonly Dictionary<string, IPEndPoint> _peers = new();
    private readonly object _lock = new();

    public RendezvousServer(int port = 5555)
    {
        _udpServer = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        Console.WriteLine($"[Server] Started on port {port}");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpServer.ReceiveAsync(cancellationToken);
                await ProcessMessageAsync(result.Buffer, result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server Error] {ex.Message}");
            }
        }
    }

    private async Task ProcessMessageAsync(byte[] data, IPEndPoint sender)
    {
        var message = JsonSerializer.Deserialize<Message>(Encoding.UTF8.GetString(data));
        if (message == null) return;

        Console.WriteLine($"[Server] Received {message.Type} from {sender}");

        switch (message.Type)
        {
            case "REGISTER":
                await HandleRegisterAsync(message, sender);
                break;
                
            case "GET_PEER":
                await HandleGetPeerAsync(message, sender);
                break;
                
            case "PUNCH_NOTIFY":
                await HandlePunchNotifyAsync(message, sender);
                break;
        }
    }

    private async Task HandleRegisterAsync(Message message, IPEndPoint sender)
    {
        lock (_lock)
        {
            _peers[message.PeerName] = sender;
        }
        
        Console.WriteLine($"[Server] Registered {message.PeerName} at {sender}");
        
        var response = new Message
        {
            Type = "REGISTERED",
            PeerName = message.PeerName
        };
        
        await SendAsync(response, sender);
    }

    private async Task HandleGetPeerAsync(Message message, IPEndPoint sender)
    {
        lock (_lock)
        {
            if (_peers.TryGetValue(message.TargetPeer, out var peerEndpoint))
            {
                Console.WriteLine($"[Server] Sending {message.TargetPeer} address {peerEndpoint} to {sender}");
                
                var response = new Message
                {
                    Type = "PEER_INFO",
                    PeerName = message.TargetPeer,
                    PeerIp = peerEndpoint.Address.ToString(),
                    PeerPort = peerEndpoint.Port
                };
                
                // Отправляем информацию запрашивающему
                SendAsync(response, sender);
                
                // Также уведомляем целевой пир, что кто-то хочет с ним соединиться
                var notification = new Message
                {
                    Type = "PEER_WANTS_CONNECT",
                    PeerName = message.PeerName,
                    PeerIp = sender.Address.ToString(),
                    PeerPort = sender.Port
                };
                
                SendAsync(notification, peerEndpoint);
            }
            else
            {
                Console.WriteLine($"[Server] Peer {message.TargetPeer} not found");
                
                var response = new Message
                {
                    Type = "ERROR",
                    Data = $"Peer {message.TargetPeer} not found"
                };
                
                SendAsync(response, sender);
            }
        }
    }

    private async Task HandlePunchNotifyAsync(Message message, IPEndPoint sender)
    {
        Console.WriteLine($"[Server] Peer {message.PeerName} is punching to {message.TargetPeer}");
        // Можно логировать или пересылать статус
    }

    private async Task SendAsync(Message message, IPEndPoint target)
    {
        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);
        await _udpServer.SendAsync(data, data.Length, target);
    }
}