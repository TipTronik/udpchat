using UdpHolePunching.Client;
using UdpHolePunching.STUN;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  dotnet run server              - Run STUN-aware server");
            Console.WriteLine("  dotnet run client <name>       - Run STUN-aware client");
            Console.WriteLine("  dotnet run test                - Test STUN discovery");
            return;
        }
        
        if (args.Length > 0 && args[0] == "diag")
        {
            // Запуск диагностики
            var stun = new DiagnosticStunClient(timeoutMs: 3000, verbose: true);
            await stun.DiscoverAsync();
            
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            return;
        }

        switch (args[0].ToLower())
        {
            case "server":
                await RunServerAsync();
                break;
                
            case "client":
                if (args.Length > 1)
                    await RunClientAsync(args[1]);
                else
                    Console.WriteLine("Please specify client name");
                break;
                
            case "test":
                await TestStunAsync();
                break;
        }
    }

    static async Task RunServerAsync()
    {
        var server = new StunAwareRendezvousServer();
        Console.WriteLine("Server started. Press Ctrl+C to stop.");
        await server.StartAsync();
    }

    static async Task RunClientAsync(string name)
    {
        using var client = new StunAwarePeerClient(name);
        
        client.OnLog += (s, msg) => { /* Already logged */ };
        client.OnMessageReceived += (s, e) =>
        {
            Console.WriteLine($"\n💬 {e.Peer}: {e.Message}\n> ");
        };

        await client.StartAsync();

        Console.WriteLine("\nCommands:");
        Console.WriteLine("  connect <peer>   - Connect to peer");
        Console.WriteLine("  send <peer> <msg> - Send message");
        Console.WriteLine("  status           - Show NAT status");
        Console.WriteLine("  exit             - Exit");

        while (true)
        {
            Console.Write("> ");
            var cmd = Console.ReadLine()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cmd == null || cmd.Length == 0) continue;

            switch (cmd[0].ToLower())
            {
                case "connect":
                    if (cmd.Length > 1)
                        await client.ConnectToPeerAsync(cmd[1]);
                    break;
                    
                case "send":
                    if (cmd.Length > 2)
                    {
                        var msg = string.Join(' ', cmd.Skip(2));
                        await client.SendP2PMessageAsync(cmd[1], msg);
                    }
                    break;
                    
                case "status":
                    Console.WriteLine(client.GetStatus());
                    break;
                    
                case "exit":
                    return;
            }
        }
    }

    static async Task TestStunAsync()
    {
        Console.WriteLine("=== STUN Test ===");
        
        var stun = new StunClient();
        stun.OnLog += (s, msg) => Console.WriteLine(msg);
        
        if (await stun.DiscoverAsync())
        {
            Console.WriteLine($"\n✅ STUN successful!");
            Console.WriteLine($"Public endpoint: {stun.PublicEndpoint}");
            Console.WriteLine($"NAT type: {stun.NatType}");
        }
        else
        {
            Console.WriteLine("\n❌ STUN failed");
        }
    }
}