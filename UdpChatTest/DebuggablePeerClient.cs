// Расширенный класс с диагностикой

using System.Net;

public class DebuggablePeerClient : PurePeerClient
{
    private readonly string _logFile;
    private readonly bool _verbose;

    public DebuggablePeerClient(string peerName, string rendezvousHost = "127.0.0.1", 
        int rendezvousPort = 5555, bool verbose = true) 
        : base(peerName, rendezvousHost, rendezvousPort)
    {
        _verbose = verbose;
        _logFile = $"p2p_debug_{peerName}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        
        // Перехватываем все логи
        OnLog += (s, msg) => {
            File.AppendAllText(_logFile, $"{DateTime.Now:HH:mm:ss.fff} - {msg}\n");
            if (_verbose) Console.WriteLine(msg);
        };
        
        OnMessageReceived += (s, e) => {
            File.AppendAllText(_logFile, $"{DateTime.Now:HH:mm:ss.fff} - RECEIVED: {e.Peer}: {e.Message}\n");
        };
        
        OnError += (s, e) => {
            File.AppendAllText(_logFile, $"{DateTime.Now:HH:mm:ss.fff} - ERROR: {e}\n");
        };
    }
    
    public new async Task StartAsync(string? turnServerUrl = null)
    {
        Log($"=== Starting client {_peerName} ===");
        Log($"OS: {Environment.OSVersion}");
        Log($".NET: {Environment.Version}");
        Log($"Local IPs: {string.Join(", ", GetLocalIPAddresses())}");
        await base.StartAsync(turnServerUrl);
    }
    
    private IEnumerable<IPAddress> GetLocalIPAddresses()
    {
        return Dns.GetHostEntry(Dns.GetHostName()).AddressList
            .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    }
    
    protected void Log(string message) => base.Log(message);
    
    public event EventHandler<string>? OnError;
}