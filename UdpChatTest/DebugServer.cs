using System.Net;
using System.Net.Sockets;
using System.Text;

public class DebugServer
{
    private readonly UdpClient _server;
    private readonly Dictionary<string, IPEndPoint> _clients = new();
    
    public DebugServer(int port = 5555)
    {
        _server = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        Console.WriteLine($"[Server] Started on port {port}");
    }
    
    public async Task StartAsync()
    {
        while (true)
        {
            try
            {
                var result = await _server.ReceiveAsync();
                string text = Encoding.UTF8.GetString(result.Buffer);
                Console.WriteLine($"[Server] Received from {result.RemoteEndPoint}: {text}");
                
                var parts = text.Split(',');
                if (parts.Length < 2) continue;
                var name = parts[1];
                
                switch (parts[0])
                {
                    case "REGISTER":
                        _clients[name] = result.RemoteEndPoint;
                        Console.WriteLine($"[Server] Registered {name} at {result.RemoteEndPoint}");
                        break;
                        
                    case "GET_PEER":
                        var target = parts[1];
                        if (_clients.TryGetValue(target, out var targetEndpoint))
                        {
                            // Отправляем информацию запросившему
                            string response = $"PEER|{target}|{targetEndpoint.Address}|{targetEndpoint.Port}";
                            Console.WriteLine(response);
                            byte[] respData = Encoding.UTF8.GetBytes(response);
                            await _server.SendAsync(respData, respData.Length, result.RemoteEndPoint);
                            
                            // Также уведомляем целевого клиента
                            string notify = $"PEER|{name}|{result.RemoteEndPoint.Address}|{result.RemoteEndPoint.Port}";
                            byte[] notifyData = Encoding.UTF8.GetBytes(notify);
                            await _server.SendAsync(notifyData, notifyData.Length, targetEndpoint);
                            
                            Console.WriteLine($"[Server] Connected {name} with {target}");
                        }
                        else
                        {
                            Console.WriteLine($"[Server] Peer {target} not found");
                        }
                        break;
                        
                    case "PING":
                        byte[] pong = Encoding.UTF8.GetBytes("PONG");
                        await _server.SendAsync(pong, pong.Length, result.RemoteEndPoint);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] Error: {ex.Message}");
            }
        }
    }
}