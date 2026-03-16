using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UdpHolePunching.Server;

namespace UdpHolePunching.Client;

public class PeerClient : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly string _peerName;
    private readonly IPEndPoint _rendezvousEndpoint;
    private CancellationTokenSource _cts = new();
    private bool _isRunning;
    private IPEndPoint? _remotePeerEndpoint;

    public PeerClient(string peerName, string rendezvousHost = "127.0.0.1", int rendezvousPort = 5555, int localPort = 0)
    {
        _peerName = peerName;
        _rendezvousEndpoint = new IPEndPoint(
            //Dns.GetHostAddresses(rendezvousHost)[0], 
            IPAddress.Parse(rendezvousHost),
            rendezvousPort);
        
        _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        
        Console.WriteLine($"[{_peerName}] Local endpoint: {_udpClient.Client.LocalEndPoint}");
    }

    public async Task StartAsync()
    {
        _isRunning = true;
        _cts = new CancellationTokenSource();
        
        // Регистрируемся на сервере
        await RegisterWithServerAsync();
        
        // Запускаем прием сообщений
        _ = Task.Run(ReceiveLoopAsync);
    }

    private async Task RegisterWithServerAsync()
    {
        var registerMsg = new Message
        {
            Type = "REGISTER",
            PeerName = _peerName
        };
        
        await SendToRendezvousAsync(registerMsg);
        Console.WriteLine($"[{_peerName}] Registered with rendezvous server");
    }

    public async Task ConnectToPeerAsync(string targetPeerName)
    {
        Console.WriteLine($"[{_peerName}] Requesting address of {targetPeerName}");
        
        // Запрашиваем адрес пира у сервера
        var requestMsg = new Message
        {
            Type = "GET_PEER",
            PeerName = _peerName,
            TargetPeer = targetPeerName
        };
        
        await SendToRendezvousAsync(requestMsg);
    }

    private async Task ReceiveLoopAsync()
    {
        while (_isRunning && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(_cts.Token);
                await ProcessMessageAsync(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_peerName}] Receive error: {ex.Message}");
            }
        }
    }

    private async Task ProcessMessageAsync(byte[] data, IPEndPoint sender)
    {
        // Проверяем, не от пира ли это прямое сообщение
        if (_remotePeerEndpoint != null && sender.Equals(_remotePeerEndpoint))
        {
            // Прямое P2P сообщение!
            string text = Encoding.UTF8.GetString(data);
            Console.WriteLine($"[{_peerName}] 📨 P2P Message from {sender}: {text}");
            
            // Эхо-ответ для теста
            if (text.StartsWith("PING"))
            {
                await SendP2PMessageAsync("PONG " + DateTime.Now.Ticks);
            }
            return;
        }
        
        // Иначе это сообщение от сервера
        var message = JsonSerializer.Deserialize<Message>(Encoding.UTF8.GetString(data));
        if (message == null) return;

        Console.WriteLine($"[{_peerName}] Received from server: {message.Type}");

        switch (message.Type)
        {
            case "PEER_INFO":
                await HandlePeerInfoAsync(message);
                break;
                
            case "PEER_WANTS_CONNECT":
                await HandlePeerWantsConnectAsync(message);
                break;
                
            case "REGISTERED":
                Console.WriteLine($"[{_peerName}] Successfully registered");
                break;
                
            case "ERROR":
                Console.WriteLine($"[{_peerName}] Server error: {message.Data}");
                break;
        }
    }

    private async Task HandlePeerInfoAsync(Message message)
    {
        var remoteEndpoint = new IPEndPoint(
            IPAddress.Parse(message.PeerIp), 
            message.PeerPort);
            
        Console.WriteLine($"[{_peerName}] Got peer address: {remoteEndpoint}");
        _remotePeerEndpoint = remoteEndpoint;
        
        // Начинаем процесс hole punching
        await PerformHolePunchingAsync(remoteEndpoint);
    }

    private async Task HandlePeerWantsConnectAsync(Message message)
    {
        var remoteEndpoint = new IPEndPoint(
            IPAddress.Parse(message.PeerIp), 
            message.PeerPort);
            
        Console.WriteLine($"[{_peerName}] Peer {message.PeerName} wants to connect from {remoteEndpoint}");
        _remotePeerEndpoint = remoteEndpoint;
        
        // Тоже начинаем пробивать дыру
        await PerformHolePunchingAsync(remoteEndpoint);
    }

    private async Task PerformHolePunchingAsync(IPEndPoint remoteEndpoint)
    {
        Console.WriteLine($"[{_peerName}] 🔨 Starting UDP hole punching to {remoteEndpoint}");
        
        // Уведомляем сервер, что начинаем пробивку
        var notifyMsg = new Message
        {
            Type = "PUNCH_NOTIFY",
            PeerName = _peerName,
            TargetPeer = "target"
        };
        await SendToRendezvousAsync(notifyMsg);
        
        // Начинаем слать "пробивочные" пакеты
        // Это создает mapping в NAT
        for (int i = 0; i < 10; i++)
        {
            byte[] punchData = Encoding.UTF8.GetBytes($"HOLE_PUNCH_{i}");
            await _udpClient.SendAsync(punchData, punchData.Length, remoteEndpoint);
            Console.WriteLine($"[{_peerName}] 👊 Punch {i + 1}/10 sent");
            await Task.Delay(100); // Небольшая пауза между пакетами
        }
        
        // После пробивки пробуем установить соединение
        Console.WriteLine($"[{_peerName}] ✅ Hole punching complete, sending test message...");
        await SendP2PMessageAsync($"PING from {_peerName}");
        
        // Запускаем периодическую отправку keep-alive
        _ = Task.Run(async () =>
        {
            while (_isRunning && _remotePeerEndpoint != null)
            {
                await Task.Delay(5000);
                if (_remotePeerEndpoint != null)
                {
                    await SendP2PMessageAsync("KEEP_ALIVE");
                }
            }
        });
    }

    public async Task SendP2PMessageAsync(string message)
    {
        if (_remotePeerEndpoint == null)
        {
            Console.WriteLine($"[{_peerName}] No remote peer connected");
            return;
        }
        
        byte[] data = Encoding.UTF8.GetBytes(message);
        await _udpClient.SendAsync(data, data.Length, _remotePeerEndpoint);
        Console.WriteLine($"[{_peerName}] Sent P2P message: {message}");
    }

    private async Task SendToRendezvousAsync(Message message)
    {
        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);
        Console.WriteLine(_rendezvousEndpoint);
        await _udpClient.SendAsync(data, data.Length, _rendezvousEndpoint);
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        _udpClient?.Dispose();
    }
}