using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UdpHolePunching.Pure;

public class PureRendezvousServer
{
    private readonly UdpClient _udpServer;
    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly object _lock = new();

    public PureRendezvousServer(int port = 5555)
    {
        _udpServer = new UdpClient(new IPEndPoint(IPAddress.Any, port));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Server] Started on port 5555");

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
        var message = JsonSerializer.Deserialize<PeerMessage>(Encoding.UTF8.GetString(data));
        if (message == null) return;

        switch (message.Type)
        {
            case "REGISTER":
                lock (_lock)
                {
                    _peers[message.Sender] = new PeerInfo
                    {
                        PeerName = message.Sender,
                        ReportedEndpoint = message.InternalEndpoint != null
                            ? IPEndPoint.Parse(message.InternalEndpoint)
                            : sender,
                        NatType = message.NatType ?? NatType.Unknown,
                        LastSeen = DateTime.UtcNow
                    };
                }

                Console.WriteLine($"[Server] Registered {message.Sender} at {sender}");
                await SendAsync(new PeerMessage { Type = "REGISTERED", Sender = "server" }, sender);
                break;

            case "GET_PEER":
                lock (_lock)
                {
                    if (_peers.TryGetValue(message.Target!, out var peer))
                    {
                        var response = new PeerMessage
                        {
                            Type = "PEER_INFO",
                            Sender = peer.PeerName,
                            ExternalEndpoint = peer.ReportedEndpoint?.ToString(),
                            NatType = peer.NatType
                        };
                        SendAsync(response, sender);

                        // Уведомляем целевой пир
                        var notify = new PeerMessage
                        {
                            Type = "PEER_INFO",
                            Sender = message.Sender,
                            ExternalEndpoint = sender.ToString(),
                            NatType = message.NatType
                        };
                        SendAsync(notify, peer.ReportedEndpoint!);
                    }
                    else
                    {
                        SendAsync(new PeerMessage
                        {
                            Type = "ERROR",
                            Sender = "server",
                            Data = $"Peer {message.Target} not found"
                        }, sender);
                    }
                }

                break;
        }
    }

    private async Task SendAsync(PeerMessage message, IPEndPoint target)
    {
        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);
        await _udpServer.SendAsync(data, data.Length, target);
    }
}