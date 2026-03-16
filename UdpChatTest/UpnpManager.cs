using System.Net;
using System.Net.Sockets;
using System.Text;

public class UpnpManager : IDisposable
{
    private UdpClient? _upnpClient;
    private IPEndPoint? _routerEndpoint;
    private IPAddress? _externalIp;
    private int _mappedPort;
    private Timer? _renewTimer;

    public event EventHandler<string>? OnLog;
    public event EventHandler<IPAddress>? OnExternalIpObtained;

    public bool IsAvailable => _routerEndpoint != null;

    public async Task<bool> DiscoverAsync(int localPort, int desiredExternalPort, TimeSpan timeout)
    {
        try
        {
            OnLog?.Invoke(this, "🔍 UPnP: Discovering routers...");

            _upnpClient = new UdpClient();
            _upnpClient.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            
            // M-SEARCH запрос для UPnP
            string searchMessage = 
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 3\r\n" +
                "ST: urn:schemas-upnp-org:service:WANIPConnection:1\r\n" +
                "\r\n";

            byte[] searchData = Encoding.ASCII.GetBytes(searchMessage);
            var multicastEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            
            await _upnpClient.SendAsync(searchData, searchData.Length, multicastEndpoint);

            var cts = new CancellationTokenSource(timeout);
            
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _upnpClient.ReceiveAsync(cts.Token);
                    string response = Encoding.ASCII.GetString(result.Buffer);
                    
                    if (response.Contains("urn:schemas-upnp-org:service:WANIPConnection:1"))
                    {
                        // Нашли роутер, парсим LOCATION для получения описания
                        var location = ParseLocation(response);
                        if (location != null)
                        {
                            _routerEndpoint = new IPEndPoint(result.RemoteEndPoint.Address, location.Value.Port);
                            OnLog?.Invoke(this, $"✅ UPnP: Router found at {_routerEndpoint}");
                            
                            // Получаем внешний IP
                            await GetExternalIpAsync();
                            
                            // Пробуем создать порт mapping
                            if (await AddPortMappingAsync(localPort, desiredExternalPort))
                            {
                                StartRenewTimer(localPort, desiredExternalPort);
                                return true;
                            }
                        }
                    }
                }
                catch (SocketException)
                {
                    // Таймаут, продолжаем ждать
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"❌ UPnP discovery error: {ex.Message}");
            return false;
        }
    }

    private (string Host, int Port)? ParseLocation(string response)
    {
        var lines = response.Split('\r', '\n');
        foreach (var line in lines)
        {
            if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Substring(9).Trim().Split(':');
                if (parts.Length >= 2)
                {
                    var host = parts[1].TrimStart('/');
                    var port = parts.Length > 2 ? int.Parse(parts[2]) : 80;
                    return (host, port);
                }
            }
        }
        return null;
    }

    private async Task GetExternalIpAsync()
    {
        try
        {
            // Отправляем SOAP запрос для получения внешнего IP
            string soapRequest = 
                "<?xml version=\"1.0\"?>\r\n" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                "<s:Body>\r\n" +
                "<u:GetExternalIPAddress xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">\r\n" +
                "</u:GetExternalIPAddress>\r\n" +
                "</s:Body>\r\n" +
                "</s:Envelope>\r\n";

            using var client = new TcpClient();
            await client.ConnectAsync(_routerEndpoint!.Address, _routerEndpoint.Port);
            
            using var stream = client.GetStream();
            string httpRequest = 
                $"POST /upnp/control/WANIPConnection HTTP/1.1\r\n" +
                $"Host: {_routerEndpoint.Address}:{_routerEndpoint.Port}\r\n" +
                $"Content-Type: text/xml; charset=\"utf-8\"\r\n" +
                $"Content-Length: {soapRequest.Length}\r\n" +
                $"SOAPACTION: \"urn:schemas-upnp-org:service:WANIPConnection:1#GetExternalIPAddress\"\r\n" +
                $"\r\n" +
                $"{soapRequest}";

            byte[] requestData = Encoding.UTF8.GetBytes(httpRequest);
            await stream.WriteAsync(requestData, 0, requestData.Length);

            byte[] buffer = new byte[8192];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // Парсим IP из ответа
            var ipMatch = System.Text.RegularExpressions.Regex.Match(response, 
                @"<NewExternalIPAddress>(.*?)</NewExternalIPAddress>");
            
            if (ipMatch.Success && IPAddress.TryParse(ipMatch.Groups[1].Value, out var ip))
            {
                _externalIp = ip;
                OnExternalIpObtained?.Invoke(this, ip);
                OnLog?.Invoke(this, $"🌐 UPnP: External IP = {ip}");
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"⚠️ UPnP: Failed to get external IP: {ex.Message}");
        }
    }

    private async Task<bool> AddPortMappingAsync(int internalPort, int externalPort)
    {
        try
        {
            string soapRequest = 
                "<?xml version=\"1.0\"?>\r\n" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                "<s:Body>\r\n" +
                "<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">\r\n" +
                $"<NewRemoteHost></NewRemoteHost>\r\n" +
                $"<NewExternalPort>{externalPort}</NewExternalPort>\r\n" +
                $"<NewProtocol>UDP</NewProtocol>\r\n" +
                $"<NewInternalPort>{internalPort}</NewInternalPort>\r\n" +
                $"<NewInternalClient>{GetLocalIpAddress()}</NewInternalClient>\r\n" +
                $"<NewEnabled>1</NewEnabled>\r\n" +
                $"<NewPortMappingDescription>UDP Hole Punching</NewPortMappingDescription>\r\n" +
                $"<NewLeaseDuration>3600</NewLeaseDuration>\r\n" +
                "</u:AddPortMapping>\r\n" +
                "</s:Body>\r\n" +
                "</s:Envelope>\r\n";

            using var client = new TcpClient();
            await client.ConnectAsync(_routerEndpoint!.Address, _routerEndpoint.Port);
            
            using var stream = client.GetStream();
            string httpRequest = 
                $"POST /upnp/control/WANIPConnection HTTP/1.1\r\n" +
                $"Host: {_routerEndpoint.Address}:{_routerEndpoint.Port}\r\n" +
                $"Content-Type: text/xml; charset=\"utf-8\"\r\n" +
                $"Content-Length: {soapRequest.Length}\r\n" +
                $"SOAPACTION: \"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"\r\n" +
                $"\r\n" +
                $"{soapRequest}";

            byte[] requestData = Encoding.UTF8.GetBytes(httpRequest);
            await stream.WriteAsync(requestData, 0, requestData.Length);

            byte[] buffer = new byte[8192];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            _mappedPort = externalPort;
            OnLog?.Invoke(this, $"✅ UPnP: Port {externalPort} -> {internalPort} mapped");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"❌ UPnP: Port mapping failed: {ex.Message}");
            return false;
        }
    }

    private string GetLocalIpAddress()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return ip.ToString();
            }
        }
        return "0.0.0.0";
    }

    private void StartRenewTimer(int internalPort, int externalPort)
    {
        _renewTimer = new Timer(async _ =>
        {
            await AddPortMappingAsync(internalPort, externalPort);
        }, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    public async Task DeletePortMappingAsync()
    {
        if (_routerEndpoint == null || _mappedPort == 0) return;

        try
        {
            string soapRequest = 
                "<?xml version=\"1.0\"?>\r\n" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">\r\n" +
                "<s:Body>\r\n" +
                "<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">\r\n" +
                $"<NewRemoteHost></NewRemoteHost>\r\n" +
                $"<NewExternalPort>{_mappedPort}</NewExternalPort>\r\n" +
                $"<NewProtocol>UDP</NewProtocol>\r\n" +
                "</u:DeletePortMapping>\r\n" +
                "</s:Body>\r\n" +
                "</s:Envelope>\r\n";

            using var client = new TcpClient();
            await client.ConnectAsync(_routerEndpoint.Address, _routerEndpoint.Port);
            
            using var stream = client.GetStream();
            string httpRequest = 
                $"POST /upnp/control/WANIPConnection HTTP/1.1\r\n" +
                $"Host: {_routerEndpoint.Address}:{_routerEndpoint.Port}\r\n" +
                $"Content-Type: text/xml; charset=\"utf-8\"\r\n" +
                $"Content-Length: {soapRequest.Length}\r\n" +
                $"SOAPACTION: \"urn:schemas-upnp-org:service:WANIPConnection:1#DeletePortMapping\"\r\n" +
                $"\r\n" +
                $"{soapRequest}";

            byte[] requestData = Encoding.UTF8.GetBytes(httpRequest);
            await stream.WriteAsync(requestData, 0, requestData.Length);
            
            OnLog?.Invoke(this, $"🧹 UPnP: Port mapping deleted");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"⚠️ UPnP: Failed to delete mapping: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _renewTimer?.Dispose();
        _upnpClient?.Close();
    }
}