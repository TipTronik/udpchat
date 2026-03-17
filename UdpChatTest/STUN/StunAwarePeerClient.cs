using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UdpHolePunching.STUN;

namespace UdpHolePunching.Client;

public class StunAwarePeerClient : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly string _peerName;
    private readonly IPEndPoint _rendezvousEndpoint;
    private readonly StunClient _stunClient;
    private CancellationTokenSource _cts = new();
    private bool _isRunning;
    
    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly object _lock = new();
    
    // NAT info
    public IPEndPoint? PublicEndpoint => _stunClient.PublicEndpoint;
    public NatType LocalNatType => _stunClient.NatType;
    
    // События
    public event EventHandler<string>? OnLog;
    public event EventHandler<(string Peer, string Message)>? OnMessageReceived;

    public StunAwarePeerClient(
        string peerName, 
        //string rendezvousHost = "localhost", 
        string rendezvousHost = "170.168.100.38",//"127.0.0.1", 
        int rendezvousPort = 5555,
        string stunServer = "stun.ekiga.net",
        int stunPort = 3478)
    {
        _peerName = peerName;
        _rendezvousEndpoint = new IPEndPoint(
            Dns.GetHostAddresses(rendezvousHost)[0], 
            rendezvousPort);
        
        _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _stunClient = new StunClient(stunServer, stunPort);
        
        // Подключаем логи
        _stunClient.OnLog += (s, msg) => Log(msg);
    }

    private void Log(string message)
    {
        var log = $"[{_peerName}] {message}";
        Console.WriteLine(log);
        OnLog?.Invoke(this, log);
    }

    public async Task StartAsync()
    {
        Log($"Starting client with STUN support...");
        Log($"Local endpoint: {_udpClient.Client.LocalEndPoint}");

        // 1. Определяем внешний адрес и тип NAT через STUN
        Log("🌐 Discovering external address via STUN...");
        if (await _stunClient.DiscoverAsync())
        {
            Log($"✅ Public endpoint: {PublicEndpoint}");
            Log($"✅ NAT type: {LocalNatType}");
        }
        else
        {
            Log("⚠️ STUN discovery failed, will use reported endpoint");
        }

        // 2. Регистрируемся на сервере
        await RegisterWithServerAsync();

        // 3. Запускаем прием сообщений
        _isRunning = true;
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(StunRefreshLoopAsync);
        
        Log("Ready. Commands: connect <peer>, send <peer> <msg>, list, status");
    }

    private async Task RegisterWithServerAsync()
    {
        var registration = new RegistrationMessage
        {
            Type = "REGISTER",
            PeerName = _peerName,
            LocalEndpoint = ((IPEndPoint)_udpClient.Client.LocalEndPoint!)?.ToString(),
            PublicEndpoint = PublicEndpoint?.ToString(),
            NatType = LocalNatType
        };

        await SendToRendezvousAsync(registration);
        Log("Registered with rendezvous server");
    }

    public async Task ConnectToPeerAsync(string targetPeerName)
    {
        Log($"Connecting to {targetPeerName}...");

        var request = new PeerQueryMessage
        {
            Type = "GET_PEER",
            Sender = _peerName,
            TargetPeer = targetPeerName,
            SenderPublicEndpoint = PublicEndpoint,
            SenderNatType = LocalNatType
        };

        await SendToRendezvousAsync(request);
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
                Log($"Receive error: {ex.Message}");
            }
        }
    }

    private async Task ProcessMessageAsync(byte[] data, IPEndPoint sender)
    {
        string text = Encoding.UTF8.GetString(data);

        // Проверяем P2P сообщения
        if (text.StartsWith("P2P|"))
        {
            var parts = text.Split('|');
            if (parts.Length >= 3)
            {
                var fromPeer = parts[1];
                var message = parts[2];
                
                Log($"📨 P2P from {fromPeer}: {message}");
                OnMessageReceived?.Invoke(this, (fromPeer, message));
                
                // Обновляем информацию о пире
                lock (_lock)
                {
                    if (_peers.TryGetValue(fromPeer, out var peer))
                    {
                        peer.DirectEndpoint = sender;
                        peer.LastSeen = DateTime.UtcNow;
                    }
                }
            }
            return;
        }

        // Сообщения от сервера
        try
        {
            var baseMsg = JsonSerializer.Deserialize<BaseMessage>(text);
            if (baseMsg == null) return;

            switch (baseMsg.Type)
            {
                case "PEER_INFO":
                    await HandlePeerInfoAsync(JsonSerializer.Deserialize<PeerInfoMessage>(text)!);
                    break;
                    
                case "PEER_WANTS_CONNECT":
                    await HandlePeerWantsConnectAsync(JsonSerializer.Deserialize<PeerWantsConnectMessage>(text)!);
                    break;
                    
                case "REGISTERED":
                    Log("✅ Registered with server");
                    break;
                    
                case "ERROR":
                    Log($"❌ Server error: {baseMsg.Data}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            Log($"Unknown message: {text} ({ex.Message})");
        }
    }

    private async Task HandlePeerInfoAsync(PeerInfoMessage message)
    {
        // Создаем endpoint из полученной информации
        IPEndPoint? peerEndpoint = null;
        
        if (message.PublicEndpoint != null)
        {
            peerEndpoint = message.PublicEndpoint;
            Log($"Got peer {message.PeerName} public endpoint: {peerEndpoint}");
        }
        else if (message.ReportedEndpoint != null)
        {
            peerEndpoint = message.ReportedEndpoint;
            Log($"Got peer {message.PeerName} reported endpoint: {peerEndpoint}");
        }

        if (peerEndpoint == null)
        {
            Log($"No endpoint for peer {message.PeerName}");
            return;
        }

        var peerInfo = new PeerInfo
        {
            PeerName = message.PeerName,
            PublicEndpoint = message.PublicEndpoint,
            ReportedEndpoint = message.ReportedEndpoint,
            NatType = message.NatType ?? NatType.Unknown,
            State = ConnectionState.Initializing
        };

        lock (_lock)
        {
            _peers[message.PeerName] = peerInfo;
        }

        // Выбираем стратегию соединения на основе NAT типов
        await ConnectWithStrategyAsync(peerInfo);
    }

    private async Task HandlePeerWantsConnectAsync(PeerWantsConnectMessage message)
    {
        IPEndPoint? peerEndpoint = message.SenderPublicEndpoint ?? 
            new IPEndPoint(IPAddress.Parse(message.SenderIp), message.SenderPort);

        Log($"Peer {message.SenderName} wants to connect from {peerEndpoint}");

        var peerInfo = new PeerInfo
        {
            PeerName = message.SenderName,
            PublicEndpoint = message.SenderPublicEndpoint,
            ReportedEndpoint = peerEndpoint,
            NatType = message.SenderNatType ?? NatType.Unknown,
            State = ConnectionState.Initializing
        };

        lock (_lock)
        {
            _peers[message.SenderName] = peerInfo;
        }

        await ConnectWithStrategyAsync(peerInfo);
    }

    private async Task ConnectWithStrategyAsync(PeerInfo peer)
    {
        Log($"🔍 Connecting to {peer.PeerName} (NAT: {peer.NatType})");
        Log($"   Our NAT: {LocalNatType}");

        // Стратегия 1: Если у обоих публичные адреса - просто отправляем
        if (PublicEndpoint != null && peer.PublicEndpoint != null)
        {
            Log("Strategy 1: Both have public endpoints - direct connect");
            await SendP2PMessageAsync(peer.PeerName, "HELLO_DIRECT");
            peer.State = ConnectionState.Connected;
            return;
        }

        // Стратегия 2: Если один из нас Full Cone - хорошие шансы
        if (LocalNatType == NatType.FullCone || peer.NatType == NatType.FullCone)
        {
            Log("Strategy 2: One peer has Full Cone - good chances");
            await PerformEnhancedHolePunchingAsync(peer);
            peer.State = ConnectionState.HolePunching;
            return;
        }

        // Стратегия 3: Restricted/PORT Restricted против Symmetric
        if (LocalNatType == NatType.Symmetric && peer.NatType == NatType.Symmetric)
        {
            Log("Strategy 3: Both Symmetric - need TURN or port prediction");
            await PerformSymmetricPunchingAsync(peer);
            peer.State = ConnectionState.PortScanning;
        }
        else
        {
            Log("Strategy 4: Standard hole punching");
            await PerformStandardHolePunchingAsync(peer);
            peer.State = ConnectionState.HolePunching;
        }

        // Проверяем соединение
        await Task.Delay(1000);
        await TestConnectionAsync(peer.PeerName);
    }

    private async Task PerformEnhancedHolePunchingAsync(PeerInfo peer)
    {
        var targetEndpoint = peer.PublicEndpoint ?? peer.ReportedEndpoint!;
        Log($"🔨 Enhanced punching to {targetEndpoint}");

        // Отправляем пакеты с разных интервалов
        for (int burst = 0; burst < 3; burst++)
        {
            for (int i = 0; i < 15; i++)
            {
                byte[] punchData = Encoding.UTF8.GetBytes($"PUNCH|{_peerName}|{burst}-{i}");
                await _udpClient.SendAsync(punchData, punchData.Length, targetEndpoint);
                
                // Также пробуем альтернативные порты
                if (i % 3 == 0)
                {
                    var altEndpoint = new IPEndPoint(targetEndpoint.Address, 
                        targetEndpoint.Port + i % 5);
                    await _udpClient.SendAsync(punchData, punchData.Length, altEndpoint);
                }
                
                await Task.Delay(i % 5 == 0 ? 200 : 30);
            }
            await Task.Delay(500);
        }
    }

    private async Task PerformStandardHolePunchingAsync(PeerInfo peer)
    {
        var targetEndpoint = peer.PublicEndpoint ?? peer.ReportedEndpoint!;
        Log($"🔨 Standard punching to {targetEndpoint}");

        for (int i = 0; i < 30; i++)
        {
            byte[] punchData = Encoding.UTF8.GetBytes($"PUNCH|{_peerName}|{i}");
            await _udpClient.SendAsync(punchData, punchData.Length, targetEndpoint);
            await Task.Delay(50);
        }
    }

    private async Task PerformSymmetricPunchingAsync(PeerInfo peer)
    {
        var baseEndpoint = peer.PublicEndpoint ?? peer.ReportedEndpoint!;
        Log($"🔨 Symmetric punching to {baseEndpoint.Address}");

        // Для symmetric NAT сканируем широкий диапазон портов
        // Используем знание о том, что порты часто инкрементятся
        var tasks = new List<Task>();
        
        for (int offset = -20; offset <= 50; offset += 2)
        {
            int port = baseEndpoint.Port + offset;
            if (port < 1024 || port > 65535) continue;
            
            var scanEndpoint = new IPEndPoint(baseEndpoint.Address, port);
            byte[] scanData = Encoding.UTF8.GetBytes($"SCAN|{_peerName}|{offset}");
            
            tasks.Add(_udpClient.SendAsync(scanData, scanData.Length, scanEndpoint));
            
            if (tasks.Count >= 10)
            {
                await Task.WhenAll(tasks);
                tasks.Clear();
                await Task.Delay(20);
            }
        }
        
        if (tasks.Any())
            await Task.WhenAll(tasks);
    }

    private async Task TestConnectionAsync(string peerName)
    {
        for (int i = 0; i < 3; i++)
        {
            await SendP2PMessageAsync(peerName, $"PING_{i}");
            await Task.Delay(500);
        }
    }

    public async Task SendP2PMessageAsync(string targetPeerName, string message)
    {
        PeerInfo? peer;
        lock (_lock)
        {
            if (!_peers.TryGetValue(targetPeerName, out peer))
            {
                Log($"No peer {targetPeerName}");
                return;
            }
        }

        byte[] data = Encoding.UTF8.GetBytes($"P2P|{_peerName}|{message}");
        
        // Пробуем прямой endpoint
        if (peer.DirectEndpoint != null)
        {
            await _udpClient.SendAsync(data, data.Length, peer.DirectEndpoint);
            return;
        }

        // Пробуем публичный
        if (peer.PublicEndpoint != null)
        {
            await _udpClient.SendAsync(data, data.Length, peer.PublicEndpoint);
            return;
        }

        // Пробуем reported
        if (peer.ReportedEndpoint != null)
        {
            await _udpClient.SendAsync(data, data.Length, peer.ReportedEndpoint);
            return;
        }

        Log($"No route to {targetPeerName}");
    }

    private async Task SendToRendezvousAsync(object message)
    {
        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);
        await _udpClient.SendAsync(data, data.Length, _rendezvousEndpoint);
    }

    private async Task StunRefreshLoopAsync()
    {
        while (_isRunning && !_cts.Token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            
            var newEndpoint = await _stunClient.RefreshEndpointAsync();
            if (newEndpoint != null && !newEndpoint.Equals(PublicEndpoint))
            {
                Log($"🔄 Public endpoint changed to {newEndpoint}");
                // Уведомляем сервер об изменении
                await RegisterWithServerAsync();
            }
        }
    }

    public string GetStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Peer {_peerName} Status ===");
        sb.AppendLine($"Local endpoint: {_udpClient.Client.LocalEndPoint}");
        sb.AppendLine($"Public endpoint: {PublicEndpoint?.ToString() ?? "unknown"}");
        sb.AppendLine($"NAT type: {LocalNatType}");
        
        lock (_lock)
        {
            sb.AppendLine($"\nPeers ({_peers.Count}):");
            foreach (var peer in _peers.Values)
            {
                var lastSeen = peer.LastSeen == DateTime.MinValue 
                    ? "never" 
                    : $"{(DateTime.UtcNow - peer.LastSeen).TotalSeconds:F0}s ago";
                sb.AppendLine($"  {peer.PeerName}: {peer.State} (last: {lastSeen})");
            }
        }
        
        return sb.ToString();
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        _udpClient?.Close();
    }
}

#region Message Classes

public class BaseMessage
{
    public string Type { get; set; } = "";
    public string? Data { get; set; }
}

public class RegistrationMessage : BaseMessage
{
    public required string PeerName { get; set; }
    public string? LocalEndpoint { get; set; }
    public string? PublicEndpoint { get; set; }
    public NatType NatType { get; set; }
}

public class PeerQueryMessage : BaseMessage
{
    public required string Sender { get; set; }
    public required string TargetPeer { get; set; }
    public IPEndPoint? SenderPublicEndpoint { get; set; }
    public NatType SenderNatType { get; set; }
}

public class PeerInfoMessage : BaseMessage
{
    public required string PeerName { get; set; }
    public IPEndPoint? ReportedEndpoint { get; set; }
    public IPEndPoint? PublicEndpoint { get; set; }
    public NatType? NatType { get; set; }
}

public class PeerWantsConnectMessage : BaseMessage
{
    public required string SenderName { get; set; }
    public required string SenderIp { get; set; }
    public int SenderPort { get; set; }
    public IPEndPoint? SenderPublicEndpoint { get; set; }
    public NatType? SenderNatType { get; set; }
}

public class PeerInfo
{
    public required string PeerName { get; set; }
    public IPEndPoint? ReportedEndpoint { get; set; }
    public IPEndPoint? PublicEndpoint { get; set; }
    public IPEndPoint? DirectEndpoint { get; set; }
    public NatType NatType { get; set; }
    public ConnectionState State { get; set; }
    public DateTime LastSeen { get; set; }
}

public enum ConnectionState
{
    Initializing,
    HolePunching,
    PortScanning,
    Connected,
    Failed
}

#endregion