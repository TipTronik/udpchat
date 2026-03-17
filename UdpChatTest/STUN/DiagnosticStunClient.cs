using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;

namespace UdpHolePunching.STUN;

public class DiagnosticStunClient
{
    private readonly List<StunServerInfo> _servers;
    private readonly int _timeoutMs;
    private readonly bool _verbose;
    
    public IPEndPoint? PublicEndpoint { get; private set; }
    public NatType NatType { get; private set; } = NatType.Unknown;
    public List<StunTestResult> TestResults { get; } = new();

    public DiagnosticStunClient(int timeoutMs = 3000, bool verbose = true)
    {
        _timeoutMs = timeoutMs;
        _verbose = verbose;
        
        // Расширенный список STUN серверов с разными портами
        _servers = new List<StunServerInfo>
        {
            // Google STUN (RFC 5389)
            new("stun.l.google.com", 19302, "Google Primary"),
            new("stun1.l.google.com", 19302, "Google Secondary"),
            new("stun2.l.google.com", 19302, "Google Secondary"),
            new("stun3.l.google.com", 19302, "Google Secondary"),
            new("stun4.l.google.com", 19302, "Google Secondary"),
            
            // Стандартные STUN порты (3478)
            new("stun.stunprotocol.org", 3478, "STUN Protocol"),
            new("stun.ekiga.net", 3478, "Ekiga"),
            new("stun.ideasip.com", 3478, "IdeasIP"),
            new("stun.schlund.de", 3478, "Schlund"),
            
            // Альтернативные порты
            new("stun.voipbuster.com", 3478, "VoIP Buster"),
            new("stun.voipstunt.com", 3478, "VoIP Stunt"),
            new("stun.cisco.com", 3478, "Cisco"),
            new("stun.cloudflare.com", 3478, "Cloudflare"),
            
            // Порты 5349 (TLS, но пробуем UDP)
            new("stun.l.google.com", 5349, "Google TLS Port"),
            new("stun.stunprotocol.org", 5349, "STUN Protocol TLS"),
            
            // Другие порты
            new("stun.sipgate.net", 10000, "SIP Gate"),
            new("stun.qq.com", 3478, "Tencent"),
            
            // Старые серверы (RFC 3489)
            new("stun.fwdnet.net", 3478, "FWD Net"),
            new("stun.xten.com", 3478, "Xten"),
        };
    }

    private void Log(string message)
    {
        if (_verbose)
            Console.WriteLine($"[STUN Diag] {message}");
    }

    public async Task<bool> DiscoverAsync()
    {
        Log($"Starting STUN discovery with {_servers.Count} servers");
        Log($"Local machine: {Environment.MachineName}");
        Log($"OS: {Environment.OSVersion}");
        Log($".NET: {Environment.Version}");
        
        // 1. Проверяем базовую UDP connectivity
        if (!await CheckUdpConnectivityAsync())
        {
            Log("❌ Basic UDP connectivity failed - check firewall");
            return false;
        }
        
        // 2. Пробуем все серверы
        foreach (var server in _servers)
        {
            Log($"\n📡 Testing ({server.Host}:{server.Port}) - {server.Description}");
            
            var stopwatch = Stopwatch.StartNew();
            var result = await TestStunServerAsync(server);
            stopwatch.Stop();
            
            result.ResponseTime = stopwatch.ElapsedMilliseconds;
            TestResults.Add(result);
            
            if (result.Success)
            {
                Log($"  ✅ SUCCESS in {result.ResponseTime}ms");
                Log($"  🌐 External IP: {result.ExternalEndpoint}");
                Log($"  📊 NAT Behavior: {result.NatBehavior}");
                
                PublicEndpoint = result.ExternalEndpoint;
                
                // После успеха пытаемся определить NAT тип
                await DetermineNatTypeAsync(server);
                return true;
            }
            else
            {
                Log($"  ❌ Failed: {result.Error}");
            }
            
            await Task.Delay(100); // Пауза между серверами
        }
        
        Log("\n❌ All STUN servers failed. Generating diagnostic report...");
        PrintDiagnosticReport();
        return false;
    }

    private async Task<bool> CheckUdpConnectivityAsync()
    {
        try
        {
            using var client = new UdpClient();
            client.Client.ReceiveTimeout = 2000;
            
            // Пробуем отправить пакет на публичный DNS (не STUN, просто проверка UDP)
            var testEndpoint = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
            byte[] testData = new byte[1];
            
            await client.SendAsync(testData, testData.Length, testEndpoint);
            
            // Не ждем ответа, просто проверяем, что отправка не упала
            Log("✅ Basic UDP send works");
            return true;
        }
        catch (Exception ex)
        {
            Log($"❌ Basic UDP send failed: {ex.Message}");
            return false;
        }
    }

    private async Task<StunTestResult> TestStunServerAsync(StunServerInfo server)
    {
        var result = new StunTestResult
        {
            Server = server,
            Success = false
        };

        try
        {
            // Получаем IP адрес сервера
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(server.Host);
            }
            catch (Exception ex)
            {
                result.Error = $"DNS resolution failed: {ex.Message}";
                return result;
            }

            // Пробуем сначала IPv4, потом IPv6
            var ipv4 = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            var ipv6 = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6);

            if (ipv4 == null && ipv6 == null)
            {
                result.Error = "No IP addresses resolved";
                return result;
            }

            // Пробуем IPv4
            if (ipv4 != null)
            {
                result = await TestStunEndpointAsync(new IPEndPoint(ipv4, server.Port), server);
                if (result.Success) return result;
            }

            // Если IPv4 не сработал, пробуем IPv6
            if (ipv6 != null)
            {
                result = await TestStunEndpointAsync(new IPEndPoint(ipv6, server.Port), server);
                if (result.Success) return result;
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"Exception: {ex.Message}";
            return result;
        }
    }

    private async Task<StunTestResult> TestStunEndpointAsync(IPEndPoint endpoint, StunServerInfo server)
    {
        var result = new StunTestResult
        {
            Server = server,
            Success = false,
            IpVersion = endpoint.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6"
        };

        try
        {
            using var client = new UdpClient();
            client.Client.ReceiveTimeout = _timeoutMs;
            
            // Пробуем разные форматы STUN запросов
            var requests = new[]
            {
                CreateStunRequest(StunVersion.Rfc5389), // Современный
                CreateStunRequest(StunVersion.Rfc3489), // Классический
                CreateStunRequest(StunVersion.Alternative) // Альтернативный
            };

            foreach (var request in requests)
            {
                try
                {
                    // Отправляем запрос
                    await client.SendAsync(request, request.Length, endpoint);
                    
                    // Ждем ответ
                    using var cts = new CancellationTokenSource(2000);
                    var response = await client.ReceiveAsync(cts.Token);
                    
                    // Парсим ответ
                    var (success, externalEndpoint, behavior) = ParseStunResponse(response.Buffer, request);
                    
                    if (success)
                    {
                        result.Success = true;
                        result.ExternalEndpoint = externalEndpoint;
                        result.NatBehavior = behavior;
                        result.UsedRequestType = request[1] == 0x01 ? "RFC5389" : "RFC3489";
                        return result;
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    result.Error = $"Timeout ({_timeoutMs}ms)";
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    public enum StunVersion
    {
        Rfc5389,    // Современный STUN
        Rfc3489,    // Классический STUN
        Alternative // Альтернативный формат
    }

    private byte[] CreateStunRequest(StunVersion version)
    {
        switch (version)
        {
            case StunVersion.Rfc5389:
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

            case StunVersion.Rfc3489:
                // Старый формат (без Magic Cookie)
                byte[] oldRequest = new byte[20];
                oldRequest[0] = 0x00; // Message Type: Binding Request
                oldRequest[1] = 0x01;
                oldRequest[2] = 0x00; // Message Length
                oldRequest[3] = 0x00;
                // Transaction ID (16 байт, первые 4 не magic cookie)
                new Random().NextBytes(oldRequest.AsSpan(4, 16));
                return oldRequest;

            case StunVersion.Alternative:
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

    private (bool success, IPEndPoint? endpoint, string behavior) ParseStunResponse(byte[] response, byte[] request)
    {
        if (response.Length < 20)
            return (false, null, "Response too short");

        // Проверяем тип ответа
        bool isSuccessResponse = (response[0] & 0x01) != 0; // Бит успеха
        
        if (!isSuccessResponse)
            return (false, null, "Error response");

        // Проверяем наличие Magic Cookie (RFC 5389)
        bool hasMagicCookie = response.Length >= 8 && 
                              response[4] == 0x21 && 
                              response[5] == 0x12 && 
                              response[6] == 0xA4 && 
                              response[7] == 0x42;

        // Проверяем Transaction ID (если есть)
        if (hasMagicCookie && request.Length >= 20)
        {
            for (int i = 8; i < 20; i++)
            {
                if (response[i] != request[i])
                    return (false, null, "Transaction ID mismatch");
            }
        }

        // Ищем MAPPED-ADDRESS или XOR-MAPPED-ADDRESS
        for (int i = 20; i < response.Length - 4; i += 4)
        {
            // XOR-MAPPED-ADDRESS (0x0020)
            if (response[i] == 0x00 && response[i + 1] == 0x20)
            {
                int length = (response[i + 2] << 8) | response[i + 3];
                if (length >= 8 && i + 4 + length <= response.Length)
                {
                    if (response[i + 5] == 0x01) // IPv4
                    {
                        int port = (response[i + 6] << 8) | response[i + 7];
                        
                        // Для XOR-MAPPED-ADDRESS делаем XOR с magic cookie
                        if (hasMagicCookie)
                            port ^= 0x2112;
                        
                        byte[] ipBytes = new byte[4];
                        Array.Copy(response, i + 8, ipBytes, 0, 4);
                        
                        if (hasMagicCookie)
                        {
                            for (int j = 0; j < 4; j++)
                                ipBytes[j] ^= 0x21;
                        }
                        
                        return (true, new IPEndPoint(new IPAddress(ipBytes), port), 
                               hasMagicCookie ? "XOR-MAPPED" : "MAPPED");
                    }
                }
            }
            
            // MAPPED-ADDRESS (0x0001) - старый формат
            if (response[i] == 0x00 && response[i + 1] == 0x01)
            {
                int length = (response[i + 2] << 8) | response[i + 3];
                if (length >= 8 && i + 4 + length <= response.Length)
                {
                    if (response[i + 5] == 0x01) // IPv4
                    {
                        int port = (response[i + 6] << 8) | response[i + 7];
                        byte[] ipBytes = new byte[4];
                        Array.Copy(response, i + 8, ipBytes, 0, 4);
                        
                        return (true, new IPEndPoint(new IPAddress(ipBytes), port), "MAPPED");
                    }
                }
            }
        }

        return (false, null, "No address attribute found");
    }

    private async Task DetermineNatTypeAsync(StunServerInfo primaryServer)
    {
        Log("\n📊 Determining NAT type...");
        
        try
        {
            using var client1 = new UdpClient();
            using var client2 = new UdpClient();
            
            client1.Client.ReceiveTimeout = 2000;
            client2.Client.ReceiveTimeout = 2000;

            var serverIp = (await Dns.GetHostAddressesAsync(primaryServer.Host))
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            
            if (serverIp == null)
            {
                Log("Cannot determine NAT type - no IPv4 for primary server");
                return;
            }

            var stunEndpoint = new IPEndPoint(serverIp, primaryServer.Port);

            // Тест 1: Получаем внешний адрес с первого сокета
            var request = CreateStunRequest(StunVersion.Rfc5389);
            await client1.SendAsync(request, request.Length, stunEndpoint);
            var response1 = await client1.ReceiveAsync();
            var (success1, endpoint1, _) = ParseStunResponse(response1.Buffer, request);

            if (!success1)
            {
                Log("Failed to get initial endpoint");
                return;
            }

            Log($"Test 1: {endpoint1}");

            // Тест 2: Тот же сокет, другой сервер (если есть)
            // Для простоты используем тот же, но с другим портом

            // Тест 3: Другой локальный порт
            var request2 = CreateStunRequest(StunVersion.Rfc5389);
            await client2.SendAsync(request2, request2.Length, stunEndpoint);
            var response2 = await client2.ReceiveAsync();
            var (success2, endpoint2, _) = ParseStunResponse(response2.Buffer, request2);

            if (!success2)
            {
                Log("Failed to get second endpoint");
                return;
            }

            Log($"Test 2 (different port): {endpoint2}");

            // Анализ
            if (endpoint1!.Port != endpoint2!.Port)
            {
                NatType = NatType.Symmetric;
                Log("🔴 NAT Type: SYMMETRIC - разные порты для разных сокетов");
            }
            else
            {
                // Дополнительный тест: пробуем отправить с того же сокета на другой порт сервера
                // (если сервер поддерживает)
                NatType = NatType.FullCone;
                Log("🟢 NAT Type: CONE (Full/Restricted) - порты стабильны");
            }
        }
        catch (Exception ex)
        {
            Log($"NAT type detection failed: {ex.Message}");
        }
    }

    private void PrintDiagnosticReport()
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("STUN DIAGNOSTIC REPORT");
        Console.WriteLine(new string('=', 60));

        Console.WriteLine($"\n📋 Tested {TestResults.Count} servers:");
        foreach (var result in TestResults.OrderBy(r => r.ResponseTime))
        {
            var status = result.Success ? "✅" : "❌";
            Console.WriteLine($"{status} {result.Server.Host}:{result.Server.Port} - {result.Server.Description}");
            Console.WriteLine($"   Response: {result.ResponseTime}ms, {result.IpVersion ?? "N/A"}");
            if (!string.IsNullOrEmpty(result.Error))
                Console.WriteLine($"   Error: {result.Error}");
            if (result.ExternalEndpoint != null)
                Console.WriteLine($"   External: {result.ExternalEndpoint}");
        }

        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("RECOMMENDATIONS:");
        Console.WriteLine(new string('=', 60));

        if (TestResults.Any(r => r.Success))
        {
            Console.WriteLine("✅ Some STUN servers work! Check firewall for others.");
        }
        else
        {
            Console.WriteLine("❌ NO STUN SERVERS RESPONDED!");
            Console.WriteLine("\nPossible causes:");
            Console.WriteLine("1. FIREWALL: Windows Defender or third-party firewall blocking UDP");
            Console.WriteLine("2. ISP: Some providers block STUN ports");
            Console.WriteLine("3. CORPORATE NETWORK: May have strict UDP policies");
            Console.WriteLine("4. ANTIVIRUS: May be blocking unknown UDP traffic");
        }

        Console.WriteLine("\n🔧 Quick fixes to try:");
        Console.WriteLine("• Temporarily disable Windows Defender Firewall");
        Console.WriteLine("• Try on a different network (mobile hotspot)");
        Console.WriteLine("• Check if UDP port 19302 is open: test with 'telnet' or online port checker");
        Console.WriteLine("• Run as administrator");
        Console.WriteLine("• Add firewall rule: 'netsh advfirewall firewall add rule name=\"STUN UDP\" protocol=udp dir=in localport=19302 action=allow'");
    }
}

public class StunServerInfo
{
    public string Host { get; }
    public int Port { get; }
    public string Description { get; }

    public StunServerInfo(string host, int port, string description = "")
    {
        Host = host;
        Port = port;
        Description = string.IsNullOrEmpty(description) ? host : description;
    }
}

public class StunTestResult
{
    public StunServerInfo Server { get; set; } = null!;
    public bool Success { get; set; }
    public IPEndPoint? ExternalEndpoint { get; set; }
    public string? Error { get; set; }
    public long ResponseTime { get; set; }
    public string? IpVersion { get; set; }
    public string? NatBehavior { get; set; }
    public string? UsedRequestType { get; set; }
}