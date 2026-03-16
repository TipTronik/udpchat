using UdpHolePunching.Client;
using UdpHolePunching.Server;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  UdpHolePunching server          - Run rendezvous server");
            Console.WriteLine("  UdpHolePunching client <name>   - Run client with name");
            return;
        }

        if (args[0] == "server")
        {
            await RunServerAsync();
        }
        else if (args[0] == "client" && args.Length > 1)
        {
            await RunClientAsync(args[1]);
        }
    }

    static async Task RunServerAsync()
    {
        var server = new RendezvousServer();
        Console.WriteLine("Rendezvous server started. Press Ctrl+C to stop.");
        
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            tcs.SetResult();
        };
        
        await Task.WhenAny(server.StartAsync(), tcs.Task);
        Console.WriteLine("Server stopped.");
    }

    static async Task RunClientAsync(string peerName)
    {
        using var client = new PeerClient(peerName);
        await client.StartAsync();
        
        Console.WriteLine($"Client {peerName} started. Commands:");
        Console.WriteLine("  connect <peer>  - Connect to another peer");
        Console.WriteLine("  send <message>  - Send message to connected peer");
        Console.WriteLine("  exit           - Exit");
        
        while (true)
        {
            var input = Console.ReadLine()?.Split(' ', 2);
            if (input == null || input.Length == 0) continue;
            
            switch (input[0].ToLower())
            {
                case "connect":
                    if (input.Length > 1)
                        await client.ConnectToPeerAsync(input[1]);
                    else
                        Console.WriteLine("Usage: connect <peer_name>");
                    break;
                    
                case "send":
                    if (input.Length > 1)
                        await client.SendP2PMessageAsync(input[1]);
                    else
                        Console.WriteLine("Usage: send <message>");
                    break;
                    
                case "exit":
                    return;
            }
        }
    }
}