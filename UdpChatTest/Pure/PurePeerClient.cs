using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UdpHolePunching.Pure;

public class PurePeerClient : IDisposable
{
    private readonly UdpClient _udpClient;
    private protected string _peerName;
    private readonly IPEndPoint _rendezvousEndpoint;
    private CancellationTokenSource _cts = new();
    private bool _isRunning;
    
    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly object _lock = new();
    
    // NAT traversal managers
    private UpnpManager? _upnp;
    private PmpManager? _pmp;
    private TurnManager? _turn;
    
    // Определение типа NAT
    private NatType _localNatType = NatType.Unknown;
    private IPEndPoint? _publicEndpoint;
    
    public event EventHandler<string>? OnLog;
    public event EventHandler<(string Peer, string Message)>? OnMessageReceived;

    public PurePeerClient(string peerName, string rendezvousHost = "localhost", int rendezvousPort = 5555)
    {
        _peerName = peerName;
        _rendezvousEndpoint = new IPEndPoint(
            Dns.GetHostAddresses(rendezvousHost)[0], 
            rendezvousPort);
        
        _udpClient = new UdpClient(0); // Выбирает случайный порт
        _udpClient.Client.ReceiveTimeout = 1000;
        
        Log($"Local endpoint: {_udpClient.Client.LocalEndPoint}");
    }

    protected void Log(string message)
    {
        var log = $"[{_peerName}] {message}";
        Console.WriteLine(log);
        OnLog?.Invoke(this, log);
    }

    public async Task StartAsync(string? turnServerUrl = null)
    {
        _isRunning = true;
        
        // Определяем тип NAT
        await DetectNatTypeAsync();
        
        // Пытаемся настроить NAT traversal методы
        await Task.WhenAll(
            SetupUpnpAsync(),
            SetupPmpAsync(),
            SetupTurnAsync(turnServerUrl)
        );
        
        // Регистрируемся на сервере
        await RegisterWithServerAsync();
        
        // Запускаем прием сообщений
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(KeepAliveLoopAsync);
        
        Log("Ready. Commands: connect <peer>, send <peer> <msg>, list, status");
    }

    private async Task DetectNatTypeAsync()
    {
        Log("🔍 Detecting NAT type...");
        
        try
        {
            // Отправляем пакет на внешний STUN сервер (можно использовать Google)
            var stunServer = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 19302);
            var testClient = new UdpClient();
            
            // Запоминаем локальный порт до отправки
            var beforeEndpoint = (IPEndPoint)testClient.Client.LocalEndPoint!;
            
            // Отправляем тестовый пакет
            byte[] testData = Encoding.UTF8.GetBytes("NAT_TEST");
            await testClient.SendAsync(testData, testData.Length, stunServer);
            
            await Task.Delay(100);
            
            // Проверяем, изменился ли локальный порт
            var afterEndpoint = (IPEndPoint)testClient.Client.LocalEndPoint!;
            
            testClient.Close();
            
            Console.WriteLine(beforeEndpoint?.ToString() ?? "null");
            Console.WriteLine(afterEndpoint?.ToString() ?? "null");
            
            // Простое определение: если порт изменился - Symmetric
            if (beforeEndpoint.Port != afterEndpoint.Port)
            {
                _localNatType = NatType.Symmetric;
                Log($"⚠️ NAT Type: Symmetric (port changed from {beforeEndpoint.Port} to {afterEndpoint.Port})");
            }
            else
            {
                _localNatType = NatType.FullCone;
                Log($"✅ NAT Type: Full/Restricted Cone");
            }
        }
        catch (Exception ex)
        {
            Log($"❌ NAT detection failed: {ex.Message}");
            _localNatType = NatType.Symmetric;
        }
    }

    private async Task SetupUpnpAsync()
    {
        _upnp = new UpnpManager();
        _upnp.OnLog += (s, msg) => Log(msg);
        _upnp.OnExternalIpObtained += (s, ip) => _publicEndpoint = new IPEndPoint(ip, ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port);
        
        var localPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;
        await _upnp.DiscoverAsync(localPort, localPort, TimeSpan.FromSeconds(5));
    }

    private async Task SetupPmpAsync()
    {
        _pmp = new PmpManager();
        _pmp.OnLog += (s, msg) => Log(msg);
        _pmp.OnExternalIpObtained += (s, ip) => 
        {
            if (_publicEndpoint == null)
                _publicEndpoint = new IPEndPoint(ip, ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port);
        };
        
        var localPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;
        await _pmp.DiscoverAsync(localPort, localPort, TimeSpan.FromSeconds(3));
    }

    private async Task SetupTurnAsync(string? turnServerUrl)
    {
        if (string.IsNullOrEmpty(turnServerUrl))
        {
            Log("ℹ️ No TURN server provided, skipping");
            return;
        }
        
        _turn = new TurnManager();
        _turn.OnLog += (s, msg) => Log(msg);
        _turn.OnRelayReady += (s, endpoint) => Log($"✅ TURN relay ready at {endpoint}");
        _turn.OnDataReceived += (s, data) =>
        {
            string message = Encoding.UTF8.GetString(data.Data);
            Log($"📨 TURN message from {data.Peer}: {message}");
            
            // Ищем пира
            lock (_lock)
            {
                foreach (var peer in _peers.Values)
                {
                    if (peer.TurnEndpoint?.Address.Equals(data.Peer.Address) == true)
                    {
                        peer.TurnEndpoint = data.Peer;
                        OnMessageReceived?.Invoke(this, (peer.PeerName, message));
                        break;
                    }
                }
            }
        };
        
        await _turn.ConnectAsync(turnServerUrl, TimeSpan.FromSeconds(10));
    }

    private async Task RegisterWithServerAsync()
    {
        var message = new PeerMessage
        {
            Type = "REGISTER",
            Sender = _peerName,
            ExternalEndpoint = _publicEndpoint?.ToString(),
            NatType = _localNatType
        };
        
        await SendToRendezvousAsync(message);
        Log("Registered with rendezvous server");
    }

    public async Task ConnectToPeerAsync(string targetPeerName)
    {
        Log($"Connecting to {targetPeerName}...");
        
        var message = new PeerMessage
        {
            Type = "GET_PEER",
            Sender = _peerName,
            Target = targetPeerName
        };
        
        await SendToRendezvousAsync(message);
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
            catch (SocketException)
            {
                // Таймаут, продолжаем
            }
            catch (Exception ex)
            {
                Log($"Receive error: {ex.Message}");
            }
        }
    }

    private async Task ProcessMessageAsync(byte[] data, IPEndPoint sender)
    {
        // Проверяем, не P2P ли это сообщение
        string text = Encoding.UTF8.GetString(data);
        
        if (text.StartsWith("P2P|"))
        {
            // Формат: P2P|<peer>|<message>
            var parts = text.Split('|');
            if (parts.Length >= 3)
            {
                var fromPeer = parts[1];
                var message = parts[2];
                
                Log($"📨 P2P from {fromPeer}: {message}");
                OnMessageReceived?.Invoke(this, (fromPeer, message));
                
                // Обновляем время последнего контакта
                lock (_lock)
                {
                    if (_peers.TryGetValue(fromPeer, out var peer))
                    {
                        peer.LastSeen = DateTime.UtcNow;
                        peer.DirectEndpoint = sender;
                    }
                }
            }
            return;
        }
        
        // Сообщение от сервера
        try
        {
            var message = JsonSerializer.Deserialize<PeerMessage>(text);
            if (message == null) return;

            switch (message.Type)
            {
                case "PEER_INFO":
                    await HandlePeerInfoAsync(message);
                    break;
                    
                case "REGISTERED":
                    Log("✅ Registered with server");
                    break;
                    
                case "ERROR":
                    Log($"❌ Server error: {message.Data}");
                    break;
            }
        }
        catch (JsonException)
        {
            Log($"Unknown message from {sender}: {text}");
        }
    }

    private async Task HandlePeerInfoAsync(PeerMessage message)
    {
        var peerEndpoint = message.ExternalEndpoint != null ?
            IPEndPoint.Parse(message.ExternalEndpoint) :
            //message.ExternalEndpoint ?? 
            new IPEndPoint(IPAddress.Parse(message.Data!), 0);
        
        Log($"Got peer {message.Sender} info: {peerEndpoint}");
        
        var peerInfo = new PeerInfo
        {
            PeerName = message.Sender,
            ReportedEndpoint = peerEndpoint,
            NatType = message.NatType ?? NatType.Unknown,
            State = ConnectionState.HolePunching
        };
        
        lock (_lock)
        {
            _peers[message.Sender] = peerInfo;
        }
        
        // Начинаем пробивку
        await PerformHolePunchingAsync(message.Sender);
    }

    private async Task PerformHolePunchingAsync(string targetPeerName)
    {
        PeerInfo? peer;
        lock (_lock)
        {
            if (!_peers.TryGetValue(targetPeerName, out peer))
                return;
        }

        Log($"🔨 Hole punching to {targetPeerName}");

        // Стратегия 1: Если у нас есть UPnP/PMP, пробуем прямой адрес
        if (_publicEndpoint != null && peer.ReportedEndpoint != null)
        {
            Log($"Strategy 1: Direct connect via {peer.ReportedEndpoint}");
            await SendP2PMessageAsync(targetPeerName, "HELLO");
            await Task.Delay(500);
        }

        // Стратегия 2: Classic hole punching
        Log("Strategy 2: Classic hole punching");
        Log(peer.ReportedEndpoint.ToString());
        for (int i = 0; i < 50; i++)
        {
            var message = new PeerMessage()
            {
                Type = "PUNCH",
                Sender = _peerName,
                Data = i.ToString()
            };
            var json = JsonSerializer.Serialize(message);
            
            byte[] punchData = Encoding.UTF8.GetBytes(json);
            await _udpClient.SendAsync(punchData, punchData.Length, peer.ReportedEndpoint!);
            await Task.Delay(10);
        }

        // Стратегия 3: Port scanning для symmetric NAT
        if (peer.NatType == NatType.Symmetric || _localNatType == NatType.Symmetric)
        {
            Log("Strategy 3: Port scanning for symmetric NAT");
            var basePort = peer.ReportedEndpoint!.Port;
            
            for (int offset = -50; offset <= 50; offset++)
            {
                if (offset == 0) continue;
                
                var scanPort = basePort + offset;
                if (scanPort < 1024 || scanPort > 65535) continue;
                
                var scanEndpoint = new IPEndPoint(peer.ReportedEndpoint.Address, scanPort);
                byte[] scanData = Encoding.UTF8.GetBytes($"SCAN|{_peerName}|{offset}");
                await _udpClient.SendAsync(scanData, scanData.Length, scanEndpoint);
                
                if (offset % 5 == 0)
                    await Task.Delay(10);
            }
        }

        // Стратегия 4: TURN relay
        if (_turn?.IsAvailable == true && peer.UseTurn)
        {
            Log("Strategy 4: TURN relay");
            peer.State = ConnectionState.TurnRelay;
            
            byte[] turnData = Encoding.UTF8.GetBytes($"TURN|{_peerName}|Hello via TURN");
            await _turn.SendToPeerAsync(peer.ReportedEndpoint!, turnData);
        }

        peer.State = ConnectionState.Connected;
        Log($"✅ Hole punching complete for {targetPeerName}");
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
            Log($"try direct {peer.DirectEndpoint}");
            await _udpClient.SendAsync(data, data.Length, peer.DirectEndpoint);
            return;
        }
        
        // Пробуем reported endpoint
        if (peer.ReportedEndpoint != null)
        {
            Log($"try direct {peer.ReportedEndpoint}");
            await _udpClient.SendAsync(data, data.Length, peer.ReportedEndpoint);
            return;
        }
        
        // Пробуем TURN
        if (_turn?.IsAvailable == true && peer.TurnEndpoint != null)
        {
            await _turn.SendToPeerAsync(peer.TurnEndpoint, data);
            return;
        }
        
        Log($"No route to {targetPeerName}");
    }

    private async Task SendToRendezvousAsync(PeerMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);
        await _udpClient.SendAsync(data, data.Length, _rendezvousEndpoint);
    }

    private async Task KeepAliveLoopAsync()
    {
        while (_isRunning && !_cts.Token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            
            lock (_lock)
            {
                foreach (var peer in _peers.Values)
                {
                    if (peer.DirectEndpoint != null && 
                        (DateTime.UtcNow - peer.LastSeen) > TimeSpan.FromSeconds(45))
                    {
                        // Отправляем keep-alive
                        byte[] keepAlive = Encoding.UTF8.GetBytes($"P2P|{_peerName}|KEEP_ALIVE");
                        _udpClient.SendAsync(keepAlive, keepAlive.Length, peer.DirectEndpoint);
                    }
                }
            }
        }
    }

    public string GetStatus()
    {
        var status = new StringBuilder();
        status.AppendLine($"Peer: {_peerName}");
        status.AppendLine($"Local endpoint: {_udpClient.Client.LocalEndPoint}");
        status.AppendLine($"Public endpoint: {_publicEndpoint?.ToString() ?? "unknown"}");
        status.AppendLine($"NAT type: {_localNatType}");
        status.AppendLine($"UPnP: {(_upnp?.IsAvailable == true ? "✅" : "❌")}");
        status.AppendLine($"PMP: {(_pmp?.IsAvailable == true ? "✅" : "❌")}");
        status.AppendLine($"TURN: {(_turn?.IsAvailable == true ? "✅" : "❌")}");
        
        lock (_lock)
        {
            status.AppendLine($"Peers: {_peers.Count}");
            foreach (var peer in _peers.Values)
            {
                var lastSeen = (DateTime.UtcNow - peer.LastSeen).TotalSeconds;
                status.AppendLine($"  {peer.PeerName}: {peer.State} (last: {lastSeen:F0}s ago)");
            }
        }
        
        return status.ToString();
    }

    public async Task CleanupAsync()
    {
        Log("Cleaning up...");
        
        _upnp?.Dispose();
        _pmp?.Dispose();
        _turn?.Dispose();
        
        await Task.Delay(100);
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        _udpClient?.Close();
        _ = CleanupAsync();
    }
}