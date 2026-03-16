using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UdpHolePunching.Client;

namespace UdpHolePunching.Server;

public class AdvancedRendezvousServer
{
    private readonly UdpClient _udpServer;
    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly Dictionary<string, List<string>> _connectionAttempts = new();
    private readonly object _lock = new();
    private readonly Random _random = new();

    public AdvancedRendezvousServer(int port = 5555)
    {
        _udpServer = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        Console.WriteLine($"[Advanced Server] Started on port {port}");
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
        var message = JsonSerializer.Deserialize<AdvancedMessage>(Encoding.UTF8.GetString(data));
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
                
            case "NAT_DETECT":
                await HandleNatDetectAsync(message, sender);
                break;
        }
    }

    private async Task HandleRegisterAsync(AdvancedMessage message, IPEndPoint sender)
    {
        lock (_lock)
        {
            var peerInfo = new PeerInfo
            {
                Name = message.PeerName,
                Endpoint = sender,
                SupportsSymmetric = message.SupportsSymmetric,
                RegisteredAt = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow
            };
            
            _peers[message.PeerName] = peerInfo;
        }
        
        Console.WriteLine($"[Server] Registered {message.PeerName} at {sender} (Symmetric: {message.SupportsSymmetric})");
        
        // Отправляем подтверждение
        var response = new AdvancedMessage
        {
            Type = "REGISTERED",
            PeerName = message.PeerName
        };
        
        await SendAsync(response, sender);
        
        // Определяем тип NAT (если нужно)
        await DetectNatTypeAsync(message.PeerName);
    }

    private async Task HandleGetPeerAsync(AdvancedMessage message, IPEndPoint sender)
    {
        lock (_lock)
        {
            if (_peers.TryGetValue(message.TargetPeer, out var peerInfo))
            {
                Console.WriteLine($"[Server] Sending {message.TargetPeer} info to {sender}");
                
                var response = new AdvancedMessage
                {
                    Type = "PEER_INFO",
                    PeerName = message.TargetPeer,
                    PeerIp = peerInfo.Endpoint.Address.ToString(),
                    PeerPort = peerInfo.Endpoint.Port,
                    SupportsSymmetric = peerInfo.SupportsSymmetric
                };
                
                SendAsync(response, sender);
                
                // Запоминаем попытку соединения
                if (!_connectionAttempts.ContainsKey(message.TargetPeer))
                    _connectionAttempts[message.TargetPeer] = new List<string>();
                    
                _connectionAttempts[message.TargetPeer].Add(message.PeerName);
                
                // Уведомляем целевой пир
                var notification = new AdvancedMessage
                {
                    Type = "PEER_WANTS_CONNECT",
                    PeerName = message.PeerName,
                    PeerIp = sender.Address.ToString(),
                    PeerPort = sender.Port,
                    SupportsSymmetric = message.SupportsSymmetric
                };
                
                SendAsync(notification, peerInfo.Endpoint);
            }
            else
            {
                Console.WriteLine($"[Server] Peer {message.TargetPeer} not found");
                
                var response = new AdvancedMessage
                {
                    Type = "ERROR",
                    Data = $"Peer {message.TargetPeer} not found"
                };
                
                SendAsync(response, sender);
            }
        }
    }

    private async Task HandlePunchNotifyAsync(AdvancedMessage message, IPEndPoint sender)
    {
        Console.WriteLine($"[Server] Peer {message.PeerName} is punching to {message.TargetPeer}");
        
        // Можем помочь с координацией
        lock (_lock)
        {
            if (_peers.TryGetValue(message.TargetPeer, out var targetPeer))
            {
                // Сообщаем целевому пиру, что начался punching
                var coordinationMsg = new AdvancedMessage
                {
                    Type = "PUNCH_STARTED",
                    PeerName = message.PeerName,
                    Data = "Start punching now"
                };
                
                SendAsync(coordinationMsg, targetPeer.Endpoint);
            }
        }
    }

    private async Task HandleNatDetectAsync(AdvancedMessage message, IPEndPoint sender)
    {
        Console.WriteLine($"[Server] NAT detection from {message.PeerName}");
        
        // Анализируем поведение NAT
        var response = new AdvancedMessage
        {
            Type = "NAT_TYPE_INFO",
            NatType = "Full Cone" // В реальности нужно анализировать
        };
        
        await SendAsync(response, sender);
    }

    private async Task DetectNatTypeAsync(string peerName)
    {
        // Здесь можно реализовать определение NAT типа
        // Отправляем тестовые пакеты на разные порты и смотрим, как меняется source port
        await Task.CompletedTask;
    }

    private async Task SendAsync(AdvancedMessage message, IPEndPoint target)
    {
        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);
        await _udpServer.SendAsync(data, data.Length, target);
    }
}

public class PeerInfo
{
    public required string Name { get; set; }
    public required IPEndPoint Endpoint { get; set; }
    public bool SupportsSymmetric { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime LastSeen { get; set; }
    public int ConnectionAttempts { get; set; }
}