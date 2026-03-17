using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UdpHolePunching.STUN;

public class StunClient
{
    private readonly UdpClient _udpClient;
    private readonly string _stunServer;
    private readonly int _stunPort;
    
    public event EventHandler<string>? OnLog;
    public IPEndPoint? PublicEndpoint { get; private set; }
    public NatType NatType { get; private set; } = NatType.Symmetric;

    public StunClient(string stunServer = "stun.ekiga.net)", int stunPort = 3478)
    {
        _stunServer = stunServer;
        _stunPort = stunPort;
        _udpClient = new UdpClient();
    }

    private void Log(string message)
    {
        OnLog?.Invoke(this, $"[STUN] {message}");
    }

    public async Task<bool> DiscoverAsync()
    {
        try
        {
            Log($"Connecting to STUN server {_stunServer}:{_stunPort}");
            
            // Получаем IP STUN сервера
            var serverIp = (await Dns.GetHostAddressesAsync(_stunServer))
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            
            if (serverIp == null)
            {
                Log("Could not resolve STUN server");
                return false;
            }

            var stunEndpoint = new IPEndPoint(serverIp, _stunPort);
            
            // Тест 1: Получаем внешний адрес
            Log("Test 1: Getting external address...");
            var result1 = await PerformStunBindingAsync(stunEndpoint);
            
            if (result1 == null)
            {
                Log("Failed to get STUN response");
                return false;
            }

            PublicEndpoint = result1;
            Log($"✅ External endpoint: {PublicEndpoint}");

            // Тест 2: Проверка на Symmetric NAT
            Log("Test 2: Testing for symmetric NAT...");
            
            // Используем другой локальный порт
            using var udpClient2 = new UdpClient();
            var result2 = await PerformStunBindingAsync(stunEndpoint, udpClient2);

            if (result2 == null)
            {
                NatType = NatType.Unknown;
                return true;
            }

            // Если внешний порт отличается для разных локальных портов - Symmetric
            if (result1.Port != result2.Port)
            {
                NatType = NatType.Symmetric;
                Log($"⚠️ NAT Type: Symmetric (port {result1.Port} vs {result2.Port})");
                return true;
            }

            // Тест 3: Проверка на Full/Restricted Cone
            Log("Test 3: Testing cone type...");
            
            // Отправляем запрос с другого сервера (если бы у нас был второй STUN)
            // Упрощенно: пробуем подключиться к другому порту того же сервера
            var altStunEndpoint = new IPEndPoint(serverIp, _stunPort + 1);
            var result3 = await PerformStunBindingAsync(altStunEndpoint, udpClient2);

            if (result3 == null)
            {
                NatType = NatType.FullCone;
                Log($"✅ NAT Type: Full Cone (all ports open)");
                return true;
            }

            if (result1.Port == result3.Port)
            {
                NatType = NatType.RestrictedCone;
                Log($"✅ NAT Type: Restricted Cone");
            }
            else
            {
                NatType = NatType.PortRestrictedCone;
                Log($"✅ NAT Type: Port Restricted Cone");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"STUN discovery failed: {ex.Message}");
            return false;
        }
    }

    private async Task<IPEndPoint?> PerformStunBindingAsync(IPEndPoint stunEndpoint, UdpClient? client = null)
    {
        client ??= _udpClient;
        
        try
        {
            // Создаем STUN Binding Request (RFC 5389)
            byte[] request = CreateStunRequest();
            
            // Пробуем разные форматы STUN запросов
            var requests = new[]
            {
                CreateStunRequest(DiagnosticStunClient.StunVersion.Rfc5389), // Современный
                CreateStunRequest(DiagnosticStunClient.StunVersion.Rfc3489), // Классический
                CreateStunRequest(DiagnosticStunClient.StunVersion.Alternative) // Альтернативный
            };
            
            client.Client.ReceiveTimeout = 3000;
            
            // Отправляем запрос
            await client.SendAsync(request, request.Length, stunEndpoint);
            
            // Ждем ответ
            var result = await client.ReceiveAsync();
            
            // Парсим ответ
            return ParseStunResponse(result.Buffer, request.AsSpan(4, 16));
        }
        catch (Exception ex)
        {
            Log($"STUN binding failed: {ex.Message}");
            return null;
        }
    }

    private byte[] CreateStunRequest()
    {
        byte[] request = new byte[20];
        
        // Message Type: Binding Request (0x0001)
        request[0] = 0x00;
        request[1] = 0x01;
        
        // Message Length: 0 (без атрибутов)
        request[2] = 0x00;
        request[3] = 0x00;
        
        // Magic Cookie (RFC 5389): 0x2112A442
        request[4] = 0x21;
        request[5] = 0x12;
        request[6] = 0xA4;
        request[7] = 0x42;
        
        // Transaction ID (12 случайных байт)
        var random = new Random();
        random.NextBytes(request.AsSpan(8, 12));
        
        return request;
    }
    
    private byte[] CreateStunRequest(DiagnosticStunClient.StunVersion version)
    {
        switch (version)
        {
            case DiagnosticStunClient.StunVersion.Rfc5389:
                // RFC 5389 Binding Request
                byte[] request = new byte[20];
                request[0] = 0x00; // Message Type: Binding Request
                request[1] = 0x01;
                request[2] = 0x00; // Message Length
                request[3] = 0x00;
                request[4] = 0x21; // Magic Cookie
                request[5] = 0x12;
                request[6] = 0xA4;
                request[7] = 0x42;
                // Transaction ID (12 случайных байт)
                new Random().NextBytes(request.AsSpan(8, 12));
                return request;

            case DiagnosticStunClient.StunVersion.Rfc3489:
                // Старый формат (без Magic Cookie)
                byte[] oldRequest = new byte[20];
                oldRequest[0] = 0x00; // Message Type: Binding Request
                oldRequest[1] = 0x01;
                oldRequest[2] = 0x00; // Message Length
                oldRequest[3] = 0x00;
                // Transaction ID (16 байт, первые 4 не magic cookie)
                new Random().NextBytes(oldRequest.AsSpan(4, 16));
                return oldRequest;

            case DiagnosticStunClient.StunVersion.Alternative:
                // Альтернативный формат (некоторые серверы так понимают)
                byte[] altRequest = new byte[20];
                altRequest[0] = 0x00;
                altRequest[1] = 0x01;
                altRequest[2] = 0x00;
                altRequest[3] = 0x00;
                // Заполняем все 16 байт transaction ID
                new Random().NextBytes(altRequest.AsSpan(4, 16));
                return altRequest;

            default:
                return Array.Empty<byte>();
        }
    }

    private IPEndPoint? ParseStunResponse(byte[] response, Span<byte> transactionId)
    {
        if (response.Length < 20) return null;
        
        // Проверяем, что это Binding Response (0x0101)
        if (response[0] != 0x01 || response[1] != 0x01)
            return null;
        
        // Проверяем Magic Cookie
        if (response[4] != 0x21 || response[5] != 0x12 || 
            response[6] != 0xA4 || response[7] != 0x42)
            return null;
        
        // Проверяем Transaction ID (опционально)
        for (int i = 0; i < 12; i++)
        {
            if (response[8 + i] != transactionId[i])
                return null;
        }
        
        // Ищем XOR-MAPPED-ADDRESS атрибут (0x0020)
        for (int i = 20; i < response.Length - 4; i += 4)
        {
            if (response[i] == 0x00 && response[i + 1] == 0x20)
            {
                int length = (response[i + 2] << 8) | response[i + 3];
                if (length >= 8 && i + 4 + length <= response.Length)
                {
                    // Address Family (1 = IPv4)
                    if (response[i + 5] == 0x01)
                    {
                        // XOR-decoded port
                        int xPort = (response[i + 6] << 8) | response[i + 7];
                        int port = xPort ^ 0x2112;
                        
                        // XOR-decoded IP
                        byte[] ipBytes = new byte[4];
                        for (int j = 0; j < 4; j++)
                        {
                            ipBytes[j] = (byte)(response[i + 8 + j] ^ 0x21);
                        }
                        
                        return new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                }
            }
        }
        
        return null;
    }

    public async Task<IPEndPoint?> RefreshEndpointAsync()
    {
        try
        {
            var serverIp = (await Dns.GetHostAddressesAsync(_stunServer))
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            
            if (serverIp == null) return PublicEndpoint;
            
            var stunEndpoint = new IPEndPoint(serverIp, _stunPort);
            var result = await PerformStunBindingAsync(stunEndpoint);
            
            if (result != null)
            {
                PublicEndpoint = result;
            }
            
            return PublicEndpoint;
        }
        catch
        {
            return PublicEndpoint;
        }
    }
}

public enum NatType
{
    Unknown,
    None,               // Прямой доступ
    FullCone,           // Все входящие разрешены
    RestrictedCone,     // Только с тех IP, кому отправляли
    PortRestrictedCone, // Только с тех IP:Port, кому отправляли
    Symmetric          // Разные порты для разных адресов
}