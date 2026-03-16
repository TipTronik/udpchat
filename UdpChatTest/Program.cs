using UdpHolePunching.Client;
using UdpHolePunching.Server;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            ShowHelp();
            return;
        }

        switch (args[0].ToLower())
        {
            case "server":
                await RunAdvancedServerAsync();
                break;
                
            case "client":
                if (args.Length > 1)
                    await RunAdvancedClientAsync(args[1]);
                else
                    Console.WriteLine("Please specify client name");
                break;
                
            case "test":
                await RunSymmetricTestAsync();
                break;
                
            default:
                ShowHelp();
                break;
        }
    }

    static void ShowHelp()
    {
        Console.WriteLine("UDP Hole Punching with Symmetric NAT Support");
        Console.WriteLine("=============================================");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run server              - Run rendezvous server");
        Console.WriteLine("  dotnet run client <name>       - Run advanced client");
        Console.WriteLine("  dotnet run test                - Run symmetric NAT test");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  dotnet run server              # Terminal 1");
        Console.WriteLine("  dotnet run client Alice        # Terminal 2");
        Console.WriteLine("  dotnet run client Bob          # Terminal 3");
    }

    static async Task RunAdvancedServerAsync()
    {
        var server = new AdvancedRendezvousServer();
        Console.WriteLine("Advanced rendezvous server started. Press Ctrl+C to stop.");
        Console.WriteLine("Supporting symmetric NAT traversal");
        
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            tcs.SetResult();
        };
        
        await Task.WhenAny(server.StartAsync(), tcs.Task);
        Console.WriteLine("Server stopped.");
    }

    static async Task RunAdvancedClientAsync(string peerName)
    {
        using var client = new AdvancedPeerClient(peerName, "localhost", 5555);
        await client.StartAsync();
        
        Console.WriteLine($"Advanced client {peerName} started with symmetric NAT support");
        Console.WriteLine("Commands:");
        Console.WriteLine("  connect <peer>  - Connect to another peer");
        Console.WriteLine("  send <peer> <msg> - Send message to connected peer");
        Console.WriteLine("  list            - List active connections");
        Console.WriteLine("  exit           - Exit");
        
        while (true)
        {
            var input = Console.ReadLine()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
                    if (input.Length > 2)
                    {
                        var message = string.Join(' ', input.Skip(2));
                        await client.SendP2PMessageAsync(input[1], message);
                    }
                    else
                    {
                        Console.WriteLine("Usage: send <peer_name> <message>");
                    }
                    break;
                    
                case "list":
                    Console.WriteLine("Active connections feature coming soon");
                    break;
                    
                case "exit":
                    return;
            }
        }
    }

    static async Task RunSymmetricTestAsync()
    {
        Console.WriteLine("Symmetric NAT Test Mode");
        Console.WriteLine("======================");
        Console.WriteLine("This will simulate symmetric NAT behavior");
        
        // Тестовый код для проверки symmetric NAT traversal
        var testClient = new AdvancedPeerClient("TestClient", "localhost", 5555, 12345);
        await testClient.StartAsync();
        
        Console.WriteLine("Press any key to stop test...");
        Console.ReadKey();
        
        testClient.Dispose();
    }
}