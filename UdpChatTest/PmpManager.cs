using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public class PmpManager : IDisposable
{
    private UdpClient? _pmpClient;
    private IPEndPoint? _routerEndpoint;
    private IPAddress? _externalIp;
    private Timer? _renewTimer;
    private int _mappedPort;

    public event EventHandler<string>? OnLog;
    public event EventHandler<IPAddress>? OnExternalIpObtained;

    public bool IsAvailable => _routerEndpoint != null;

    public async Task<bool> DiscoverAsync(int internalPort, int externalPort, TimeSpan timeout)
    {
        try
        {
            OnLog?.Invoke(this, "🔍 PMP: Discovering NAT-PMP routers...");

            _pmpClient = new UdpClient();
            _pmpClient.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            
            // PMP использует адрес 224.0.0.1 и порт 5351
            var gateway = GetDefaultGateway();
            if (gateway == null)
            {
                OnLog?.Invoke(this, "❌ PMP: Could not find default gateway");
                return false;
            }

            _routerEndpoint = new IPEndPoint(gateway, 5351);
            OnLog?.Invoke(this, $"📍 PMP: Using gateway {gateway}");

            // Сначала получаем внешний IP через PMP
            if (await GetExternalIpAsync())
            {
                // Затем пробуем создать port mapping
                return await AddPortMappingAsync(internalPort, externalPort);
            }

            return false;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"❌ PMP discovery error: {ex.Message}");
            return false;
        }
    }

    private IPAddress? GetDefaultGateway()
    {
        try
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up);

            foreach (var ni in networkInterfaces)
            {
                var ipProps = ni.GetIPProperties();
                var gateway = ipProps.GatewayAddresses.FirstOrDefault()?.Address;
                if (gateway != null && gateway.AddressFamily == AddressFamily.InterNetwork)
                {
                    return gateway;
                }
            }
        }
        catch { }
        
        return null;
    }

    private async Task<bool> GetExternalIpAsync()
    {
        try
        {
            // PMP запрос для получения внешнего IP (версия 0, опция 0)
            byte[] request = new byte[] { 0, 0 }; // Version 0, Opcode 0 (External Address)
            
            await _pmpClient!.SendAsync(request, request.Length, _routerEndpoint);

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result = await _pmpClient.ReceiveAsync(cts.Token);
            
            if (result.Buffer.Length >= 12 && result.Buffer[1] == 128) // Ответ с External Address
            {
                // Парсим внешний IP (байты 8-11)
                byte[] ipBytes = new byte[4];
                Array.Copy(result.Buffer, 8, ipBytes, 0, 4);
                _externalIp = new IPAddress(ipBytes);
                
                OnExternalIpObtained?.Invoke(this, _externalIp);
                OnLog?.Invoke(this, $"🌐 PMP: External IP = {_externalIp}");
                return true;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"⚠️ PMP: Failed to get external IP: {ex.Message}");
        }
        return false;
    }

    private async Task<bool> AddPortMappingAsync(int internalPort, int externalPort)
    {
        try
        {
            // PMP запрос для UDP mapping (версия 0, опция 1)
            // Формат: Version(1) | Opcode(1) | Reserved(2) | Internal Port(2) | External Port(2) | Lifetime(4)
            byte[] request = new byte[12];
            request[0] = 0; // Version 0
            request[1] = 1; // Opcode 1 (Map UDP)
            // Reserved байты 2-3 уже 0
            
            // Internal Port (little-endian)
            request[4] = (byte)(internalPort & 0xFF);
            request[5] = (byte)((internalPort >> 8) & 0xFF);
            
            // External Port (little-endian)
            request[6] = (byte)(externalPort & 0xFF);
            request[7] = (byte)((externalPort >> 8) & 0xFF);
            
            // Lifetime (3600 секунд = 1 час, little-endian)
            int lifetime = 3600;
            request[8] = (byte)(lifetime & 0xFF);
            request[9] = (byte)((lifetime >> 8) & 0xFF);
            request[10] = (byte)((lifetime >> 16) & 0xFF);
            request[11] = (byte)((lifetime >> 24) & 0xFF);

            await _pmpClient!.SendAsync(request, request.Length, _routerEndpoint);

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result = await _pmpClient.ReceiveAsync(cts.Token);
            
            if (result.Buffer.Length >= 12 && result.Buffer[1] == 129) // Ответ для UDP mapping
            {
                _mappedPort = externalPort;
                StartRenewTimer(internalPort, externalPort);
                OnLog?.Invoke(this, $"✅ PMP: Port {externalPort} -> {internalPort} mapped (lifetime: {lifetime}s)");
                return true;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"❌ PMP: Port mapping failed: {ex.Message}");
        }
        return false;
    }

    private void StartRenewTimer(int internalPort, int externalPort)
    {
        _renewTimer = new Timer(async _ =>
        {
            await AddPortMappingAsync(internalPort, externalPort);
        }, null, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(25));
    }

    public async Task DeletePortMappingAsync()
    {
        if (_routerEndpoint == null || _mappedPort == 0) return;

        try
        {
            // Для удаления отправляем mapping с lifetime 0
            byte[] request = new byte[12];
            request[0] = 0; // Version 0
            request[1] = 1; // Opcode 1 (Map UDP)
            request[4] = (byte)(_mappedPort & 0xFF);
            request[5] = (byte)((_mappedPort >> 8) & 0xFF);
            request[6] = (byte)(_mappedPort & 0xFF);
            request[7] = (byte)((_mappedPort >> 8) & 0xFF);
            // Lifetime = 0 (удалить)
            
            await _pmpClient!.SendAsync(request, request.Length, _routerEndpoint);
            OnLog?.Invoke(this, $"🧹 PMP: Port mapping deleted");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"⚠️ PMP: Failed to delete mapping: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _renewTimer?.Dispose();
        _pmpClient?.Close();
    }
}