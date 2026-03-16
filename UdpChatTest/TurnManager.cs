using System.Net;
using System.Net.Sockets;
using System.Text;

public class TurnManager : IDisposable
{
    private UdpClient? _turnClient;
    private IPEndPoint? _turnServerEndpoint;
    private string _username = "";
    private string _password = "";
    private IPEndPoint? _relayEndpoint;
    private Dictionary<IPEndPoint, IPEndPoint> _allocations = new();
    private readonly object _lock = new();
    private Timer? _refreshTimer;

    public event EventHandler<string>? OnLog;
    public event EventHandler<IPEndPoint>? OnRelayReady;
    public event EventHandler<(IPEndPoint Peer, byte[] Data)>? OnDataReceived;

    public bool IsAvailable => _relayEndpoint != null;

    public async Task<bool> ConnectAsync(string turnServerUrl, TimeSpan timeout)
    {
        try
        {
            OnLog?.Invoke(this, $"🔌 TURN: Connecting to {turnServerUrl}");

            // Парсим URL: turn:username:password@host:port
            var (serverEndpoint, username, password) = ParseTurnUrl(turnServerUrl);
            _turnServerEndpoint = serverEndpoint;
            _username = username;
            _password = password;

            _turnClient = new UdpClient();
            _turnClient.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;

            // 1. Allocate Request
            if (!await AllocateAsync())
                return false;

            // 2. Запускаем прием сообщений
            _ = Task.Run(ReceiveLoop);

            // 3. Запускаем refresh timer
            _refreshTimer = new Timer(async _ => await RefreshAllocation(), 
                null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            OnLog?.Invoke(this, $"✅ TURN: Connected, relay at {_relayEndpoint}");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"❌ TURN connection failed: {ex.Message}");
            return false;
        }
    }

    private (IPEndPoint, string, string) ParseTurnUrl(string url)
    {
        // Формат: turn:username:password@host:port
        var withoutProtocol = url.Replace("turn:", "");
        var parts = withoutProtocol.Split('@');
        var credentials = parts[0].Split(':');
        var address = parts[1].Split(':');
        
        var username = credentials[0];
        var password = credentials[1];
        var host = address[0];
        var port = int.Parse(address[1]);
        
        var addresses = Dns.GetHostAddresses(host);
        var endpoint = new IPEndPoint(addresses[0], port);
        
        return (endpoint, username, password);
    }

    private async Task<bool> AllocateAsync()
    {
        // TURN Allocation запрос (RFC 5766)
        // Упрощенная реализация - в реальности нужно полное STUN/TURN сообщение
        
        byte[] request = CreateAllocateRequest();
        
        await _turnClient!.SendAsync(request, request.Length, _turnServerEndpoint);
        
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await _turnClient.ReceiveAsync(cts.Token);
        
        return ParseAllocateResponse(result.Buffer, out _relayEndpoint);
    }

    private byte[] CreateAllocateRequest()
    {
        // Создаем STUN-совместимый запрос
        List<byte> request = new List<byte>();
        
        // STUN Header
        request.Add(0x00); // Message Type (Allocate)
        request.Add(0x03); // ...
        request.Add(0x00); // Message Length (placeholder)
        request.Add(0x00);
        request.AddRange(Guid.NewGuid().ToByteArray().Take(12)); // Transaction ID
        
        // ATTRIBUTES
        // REQUESTED-TRANSPORT (UDP)
        request.Add(0x00);
        request.Add(0x19); // Type
        request.Add(0x00);
        request.Add(0x04); // Length
        request.Add(0x11); // UDP
        request.Add(0x00);
        request.Add(0x00);
        request.Add(0x00);
        
        // USERNAME
        var usernameBytes = Encoding.UTF8.GetBytes(_username);
        request.Add(0x00);
        request.Add(0x06); // Type
        request.Add((byte)((usernameBytes.Length >> 8) & 0xFF));
        request.Add((byte)(usernameBytes.Length & 0xFF));
        request.AddRange(usernameBytes);
        
        // Добавить padding если нужно
        while (request.Count % 4 != 0)
            request.Add(0x00);
        
        return request.ToArray();
    }

    private bool ParseAllocateResponse(byte[] response, out IPEndPoint? relayEndpoint)
    {
        relayEndpoint = null;
        
        if (response.Length < 20) return false;
        
        // Проверяем успешность (простой парсинг)
        if ((response[0] & 0x20) != 0) // Success response
        {
            // Ищем XOR-MAPPED-ADDRESS attribute
            for (int i = 20; i < response.Length - 4; i += 4)
            {
                if (response[i] == 0x00 && response[i + 1] == 0x20) // XOR-MAPPED-ADDRESS
                {
                    int len = (response[i + 2] << 8) | response[i + 3];
                    if (i + 4 + len <= response.Length)
                    {
                        // Парсим IPv4 адрес
                        byte family = response[i + 5];
                        if (family == 0x01) // IPv4
                        {
                            int port = (response[i + 6] << 8) | response[i + 7];
                            byte[] ipBytes = new byte[4];
                            Array.Copy(response, i + 8, ipBytes, 0, 4);
                            
                            // XOR с transaction id
                            for (int j = 0; j < 4; j++)
                                ipBytes[j] ^= response[4 + j];
                            
                            relayEndpoint = new IPEndPoint(new IPAddress(ipBytes), port);
                            return true;
                        }
                    }
                }
            }
        }
        
        return false;
    }

    private async Task ReceiveLoop()
    {
        while (_turnClient != null)
        {
            try
            {
                var result = await _turnClient.ReceiveAsync();
                
                // Проверяем, это данные от пира или сообщение TURN
                if (IsDataIndication(result.Buffer, out var peerEndpoint, out var data))
                {
                    OnDataReceived?.Invoke(this, (peerEndpoint, data));
                }
            }
            catch
            {
                break;
            }
        }
    }

    private bool IsDataIndication(byte[] packet, out IPEndPoint peerEndpoint, out byte[] data)
    {
        peerEndpoint = null!;
        data = null!;
        
        if (packet.Length < 20) return false;
        
        // Проверяем, это Data Indication (0x0017)
        if (packet[0] == 0x00 && packet[1] == 0x17)
        {
            // Парсим XOR-PEER-ADDRESS attribute
            for (int i = 20; i < packet.Length - 4; i += 4)
            {
                if (packet[i] == 0x00 && packet[i + 1] == 0x12) // XOR-PEER-ADDRESS
                {
                    int len = (packet[i + 2] << 8) | packet[i + 3];
                    if (packet[i + 5] == 0x01) // IPv4
                    {
                        int port = (packet[i + 6] << 8) | packet[i + 7];
                        byte[] ipBytes = new byte[4];
                        Array.Copy(packet, i + 8, ipBytes, 0, 4);
                        
                        // XOR с transaction id
                        for (int j = 0; j < 4; j++)
                            ipBytes[j] ^= packet[4 + j];
                        
                        peerEndpoint = new IPEndPoint(new IPAddress(ipBytes), port);
                        
                        // Остальные данные - payload
                        data = packet.Skip(i + 8 + len).ToArray();
                        return true;
                    }
                }
            }
        }
        
        return false;
    }

    public async Task SendToPeerAsync(IPEndPoint peerEndpoint, byte[] data)
    {
        if (_turnClient == null || _relayEndpoint == null) return;

        // Создаем Send Indication
        byte[] packet = CreateSendIndication(peerEndpoint, data);
        await _turnClient.SendAsync(packet, packet.Length, _turnServerEndpoint);
    }

    private byte[] CreateSendIndication(IPEndPoint peerEndpoint, byte[] data)
    {
        List<byte> packet = new List<byte>();
        
        // STUN Header (Send Indication)
        packet.Add(0x00);
        packet.Add(0x16); // Send Indication
        packet.Add(0x00);
        packet.Add(0x00); // Length placeholder
        packet.AddRange(Guid.NewGuid().ToByteArray().Take(12));
        
        // XOR-PEER-ADDRESS
        packet.Add(0x00);
        packet.Add(0x12);
        packet.Add(0x00);
        packet.Add(0x08); // Length
        packet.Add(0x00);
        packet.Add(0x01); // IPv4
        packet.Add((byte)(peerEndpoint.Port >> 8));
        packet.Add((byte)(peerEndpoint.Port & 0xFF));
        
        // XOR IP with transaction id
        byte[] ipBytes = peerEndpoint.Address.GetAddressBytes();
        for (int i = 0; i < 4; i++)
            packet.Add((byte)(ipBytes[i] ^ packet[4 + i]));
        
        // DATA
        packet.Add(0x00);
        packet.Add(0x13);
        packet.Add((byte)(data.Length >> 8));
        packet.Add((byte)(data.Length & 0xFF));
        packet.AddRange(data);
        
        return packet.ToArray();
    }

    private async Task RefreshAllocation()
    {
        if (_turnClient == null || _relayEndpoint == null) return;
        
        try
        {
            // Refresh Request
            byte[] request = new byte[20]; // Упрощенно
            request[0] = 0x00;
            request[1] = 0x04; // Refresh
            
            await _turnClient.SendAsync(request, request.Length, _turnServerEndpoint);
            OnLog?.Invoke(this, "🔄 TURN allocation refreshed");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"⚠️ TURN refresh failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _turnClient?.Close();
    }
}