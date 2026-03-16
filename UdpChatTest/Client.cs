using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UdpHolePunching.Client;

public class AdvancedPeerClient : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly string _peerName;
    private readonly IPEndPoint _rendezvousEndpoint;
    private CancellationTokenSource _cts = new();
    private bool _isRunning;
    private IPEndPoint? _remotePeerEndpoint;
    
    // Для symmetric NAT
    private readonly Dictionary<string, PeerConnection> _peerConnections = new();
    private readonly object _lock = new();
    private int _baseLocalPort;
    private readonly Random _random = new();

    public AdvancedPeerClient(string peerName, string rendezvousHost = "localhost", int rendezvousPort = 5555, int localPort = 0)
    {
        _peerName = peerName;
        _rendezvousEndpoint = new IPEndPoint(
            Dns.GetHostAddresses(rendezvousHost)[0], 
            rendezvousPort);
        
        _baseLocalPort = localPort == 0 ? new Random().Next(10000, 60000) : localPort;
        _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, _baseLocalPort));
        
        Console.WriteLine($"[{_peerName}] Local endpoint: {_udpClient.Client.LocalEndPoint}");
        Console.WriteLine($"[{_peerName}] Base port: {_baseLocalPort}");
    }

    public async Task StartAsync()
    {
        _isRunning = true;
        _cts = new CancellationTokenSource();
        
        await RegisterWithServerAsync();
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(PunchingMaintenanceAsync);
    }

    private async Task RegisterWithServerAsync()
    {
        var registerMsg = new AdvancedMessage
        {
            Type = "REGISTER",
            PeerName = _peerName,
            SupportsSymmetric = true
        };
        
        await SendToRendezvousAsync(registerMsg);
        Console.WriteLine($"[{_peerName}] Registered with rendezvous server (Symmetric NAT support)");
    }

    public async Task ConnectToPeerAsync(string targetPeerName)
    {
        Console.WriteLine($"[{_peerName}] Requesting address of {targetPeerName}");
        
        var requestMsg = new AdvancedMessage
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
        // Проверяем, не P2P ли это сообщение
        lock (_lock)
        {
            foreach (var conn in _peerConnections.Values)
            {
                if (conn.IsPotentialMatch(sender))
                {
                    // Это от известного пира!
                    string text = Encoding.UTF8.GetString(data);
                    Console.WriteLine($"[{_peerName}] 📨 P2P Message from {conn.PeerName} ({sender}): {text}");
                    
                    // Обновляем активность
                    conn.LastSeen = DateTime.UtcNow;
                    
                    // Отвечаем на PING
                    if (text.StartsWith("PING"))
                    {
                        _ = SendP2PMessageAsync(conn.PeerName, "PONG " + DateTime.Now.Ticks);
                    }
                    return;
                }
            }
        }
        
        // Сообщение от сервера
        try
        {
            var message = JsonSerializer.Deserialize<AdvancedMessage>(Encoding.UTF8.GetString(data));
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
                    
                case "NAT_TYPE_INFO":
                    Console.WriteLine($"[{_peerName}] Peer NAT type: {message.NatType}");
                    break;
                    
                case "REGISTERED":
                    Console.WriteLine($"[{_peerName}] Successfully registered");
                    break;
                    
                case "ERROR":
                    Console.WriteLine($"[{_peerName}] Server error: {message.Data}");
                    break;
            }
        }
        catch (JsonException)
        {
            // Возможно, это старый формат или прямое сообщение
            Console.WriteLine($"[{_peerName}] Received raw data from {sender}: {Encoding.UTF8.GetString(data)}");
        }
    }

    private async Task HandlePeerInfoAsync(AdvancedMessage message)
    {
        var remoteEndpoint = new IPEndPoint(
            IPAddress.Parse(message.PeerIp), 
            message.PeerPort);
            
        Console.WriteLine($"[{_peerName}] Got peer address: {remoteEndpoint}");
        Console.WriteLine($"[{_peerName}] Peer supports symmetric: {message.SupportsSymmetric}");
        
        var connection = new PeerConnection
        {
            PeerName = message.PeerName,
            ReportedEndpoint = remoteEndpoint,
            SupportsSymmetric = message.SupportsSymmetric,
            State = ConnectionState.Initializing
        };
        
        lock (_lock)
        {
            _peerConnections[message.PeerName] = connection;
        }
        
        // Начинаем процесс hole punching с учетом типа NAT
        await PerformAdvancedHolePunchingAsync(message.PeerName);
    }

    private async Task HandlePeerWantsConnectAsync(AdvancedMessage message)
    {
        var remoteEndpoint = new IPEndPoint(
            IPAddress.Parse(message.PeerIp), 
            message.PeerPort);
            
        Console.WriteLine($"[{_peerName}] Peer {message.PeerName} wants to connect from {remoteEndpoint}");
        
        var connection = new PeerConnection
        {
            PeerName = message.PeerName,
            ReportedEndpoint = remoteEndpoint,
            SupportsSymmetric = message.SupportsSymmetric,
            State = ConnectionState.Initializing
        };
        
        lock (_lock)
        {
            _peerConnections[message.PeerName] = connection;
        }
        
        await PerformAdvancedHolePunchingAsync(message.PeerName);
    }

    private async Task PerformAdvancedHolePunchingAsync(string targetPeerName)
    {
        PeerConnection? connection;
        lock (_lock)
        {
            if (!_peerConnections.TryGetValue(targetPeerName, out connection))
                return;
        }

        Console.WriteLine($"[{_peerName}] 🔨 Starting advanced UDP hole punching to {targetPeerName}");
        
        // Уведомляем сервер
        var notifyMsg = new AdvancedMessage
        {
            Type = "PUNCH_NOTIFY",
            PeerName = _peerName,
            TargetPeer = targetPeerName
        };
        await SendToRendezvousAsync(notifyMsg);

        // Стратегия 1: Классический hole punching (для Full/Restricted NAT)
        await PerformClassicPunchingAsync(connection);
        
        // Стратегия 2: Если не сработало и пир поддерживает symmetric - пробуем инкрементальный поиск
        if (connection.SupportsSymmetric)
        {
            await Task.Delay(1000); // Пауза между стратегиями
            await PerformSymmetricPunchingAsync(connection);
        }
        
        // Стратегия 3: Пробуем угадать порт по временным меткам
        await Task.Delay(1000);
        await PerformTimestampBasedPunchingAsync(connection);
        
        connection.State = ConnectionState.PunchingComplete;
        Console.WriteLine($"[{_peerName}] ✅ Hole punching strategies complete for {targetPeerName}");
    }

    private async Task PerformClassicPunchingAsync(PeerConnection connection)
    {
        Console.WriteLine($"[{_peerName}] Strategy 1: Classic hole punching");
        
        var remoteEndpoint = connection.ReportedEndpoint;
        
        // Шлем пакеты с разными интервалами
        for (int i = 0; i < 15; i++)
        {
            byte[] punchData = Encoding.UTF8.GetBytes($"CLASSIC_PUNCH_{i}");
            await _udpClient.SendAsync(punchData, punchData.Length, remoteEndpoint);
            
            if (i % 3 == 0)
                Console.WriteLine($"[{_peerName}] 👊 Classic punch {i + 1}/15");
                
            await Task.Delay(_random.Next(50, 150)); // Рандомизируем интервалы
        }
    }

    private async Task PerformSymmetricPunchingAsync(PeerConnection connection)
    {
        Console.WriteLine($"[{_peerName}] Strategy 2: Symmetric NAT port scanning");
        
        var baseEndpoint = connection.ReportedEndpoint;
        
        // Сканируем порты в диапазоне вокруг известного
        // Обычно symmetric NAT увеличивает порт на 1-2 для каждого нового destination
        for (int offset = -5; offset <= 20; offset++)
        {
            if (offset == 0) continue; // Уже пробовали в classic
            
            int scanPort = baseEndpoint.Port + offset;
            if (scanPort < 1024 || scanPort > 65535) continue;
            
            var scanEndpoint = new IPEndPoint(baseEndpoint.Address, scanPort);
            
            byte[] punchData = Encoding.UTF8.GetBytes($"SYMMETRIC_PUNCH_{offset}");
            await _udpClient.SendAsync(punchData, punchData.Length, scanEndpoint);
            
            if (offset % 5 == 0)
                Console.WriteLine($"[{_peerName}] 🔍 Scanning port {scanPort}");
            
            await Task.Delay(30); // Быстрый сканер
        }
    }

    private async Task PerformTimestampBasedPunchingAsync(PeerConnection connection)
    {
        Console.WriteLine($"[{_peerName}] Strategy 3: Timestamp-based prediction");
        
        // Некоторые NAT используют timestamp в качестве порта
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var baseEndpoint = connection.ReportedEndpoint;
        
        for (int i = 0; i < 10; i++)
        {
            // Пробуем порты, основанные на времени
            int timePort = (int)((now + i) % 10000 + 10000);
            var timeEndpoint = new IPEndPoint(baseEndpoint.Address, timePort);
            
            byte[] punchData = Encoding.UTF8.GetBytes($"TIME_PUNCH_{i}");
            await _udpClient.SendAsync(punchData, punchData.Length, timeEndpoint);
            
            await Task.Delay(50);
        }
    }

    public async Task SendP2PMessageAsync(string peerName, string message)
    {
        PeerConnection? connection;
        lock (_lock)
        {
            if (!_peerConnections.TryGetValue(peerName, out connection) || 
                connection.BestEndpoint == null)
            {
                Console.WriteLine($"[{_peerName}] No active connection to {peerName}");
                return;
            }
            connection = connection;
        }

        byte[] data = Encoding.UTF8.GetBytes(message);
        
        // Пробуем отправить на все потенциальные endpoint'ы
        var endpoints = new List<IPEndPoint> { connection.BestEndpoint };
        if (connection.AlternativeEndpoints != null)
            endpoints.AddRange(connection.AlternativeEndpoints);

        foreach (var endpoint in endpoints)
        {
            try
            {
                await _udpClient.SendAsync(data, data.Length, endpoint);
                Console.WriteLine($"[{_peerName}] Sent to {peerName} via {endpoint}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_peerName}] Failed to send via {endpoint}: {ex.Message}");
            }
        }
    }

    private async Task SendToRendezvousAsync(AdvancedMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);
        await _udpClient.SendAsync(data, data.Length, _rendezvousEndpoint);
    }

    private async Task PunchingMaintenanceAsync()
    {
        while (_isRunning && !_cts.Token.IsCancellationRequested)
        {
            await Task.Delay(10000); // Каждые 10 секунд
            
            lock (_lock)
            {
                foreach (var conn in _peerConnections.Values)
                {
                    // Если давно не было сообщений, пробуем переустановить соединение
                    if (conn.LastSeen != null && 
                        (DateTime.UtcNow - conn.LastSeen) > TimeSpan.FromSeconds(30))
                    {
                        Console.WriteLine($"[{_peerName}] Connection to {conn.PeerName} stale, re-punching...");
                        _ = Task.Run(() => PerformAdvancedHolePunchingAsync(conn.PeerName));
                    }
                    
                    // Отправляем keep-alive
                    if (conn.BestEndpoint != null)
                    {
                        byte[] keepAlive = Encoding.UTF8.GetBytes("KEEP_ALIVE");
                        _udpClient.SendAsync(keepAlive, keepAlive.Length, conn.BestEndpoint);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        _udpClient?.Dispose();
    }
}

public class PeerConnection
{
    public required string PeerName { get; set; }
    public IPEndPoint? ReportedEndpoint { get; set; }
    public IPEndPoint? BestEndpoint { get; set; }
    public List<IPEndPoint> AlternativeEndpoints { get; set; } = new();
    public bool SupportsSymmetric { get; set; }
    public ConnectionState State { get; set; }
    public DateTime? LastSeen { get; set; }
    public int PunchAttempts { get; set; }

    public bool IsPotentialMatch(IPEndPoint endpoint)
    {
        if (BestEndpoint != null && BestEndpoint.Equals(endpoint))
            return true;
            
        return AlternativeEndpoints.Any(e => e.Equals(endpoint));
    }
}

public enum ConnectionState
{
    Initializing,
    PunchingInProgress,
    PunchingComplete,
    Connected,
    Failed
}

public class AdvancedMessage
{
    public string Type { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string TargetPeer { get; set; } = "";
    public string PeerIp { get; set; } = "";
    public int PeerPort { get; set; }
    public string Data { get; set; } = "";
    public bool SupportsSymmetric { get; set; }
    public string NatType { get; set; } = "";
    public int SequenceNumber { get; set; }
}