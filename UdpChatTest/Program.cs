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
                await RunServerAsync();
                break;
                
            case "client":
                if (args.Length > 1)
                    await RunClientAsync(args[1]);
                else
                    Console.WriteLine("Please specify client name");
                break;
                
            default:
                ShowHelp();
                break;
        }
    }

    static void ShowHelp()
    {
        Console.WriteLine("""
            Pure .NET UDP Hole Punching (No external libraries)
            ===================================================
            Usage:
              dotnet run server              - Run rendezvous server
              dotnet run client <name>       - Run client
            
            Example:
              dotnet run server              # Terminal 1
              dotnet run client Alice         # Terminal 2
              dotnet run client Bob           # Terminal 3
            """);
    }

    static async Task RunServerAsync()
    {
        var server = new PureRendezvousServer();
        Console.WriteLine("Pure .NET server started. Press Ctrl+C to stop.");
        
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
        // Для TURN можно указать сервер (опционально)
        string? turnServer = null;
        
        Console.WriteLine("Enter TURN server (or press Enter to skip):");
        Console.WriteLine("Format: turn:username:password@host:port");
        var input = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(input))
            turnServer = input;

        using var client = new PurePeerClient(peerName, 
            //"170.168.100.38", 
            "127.0.0.1",
            5555);
        
        client.OnLog += (s, msg) => { /* уже выводится */ };
        client.OnMessageReceived += (s, e) => 
        {
            Console.WriteLine($"\n💬 {e.Peer}: {e.Message}\n> ");
        };

        await client.StartAsync(turnServer);
        
        Console.WriteLine("\nCommands:");
        Console.WriteLine("  connect <peer>   - Connect to peer");
        Console.WriteLine("  send <peer> <msg> - Send message");
        Console.WriteLine("  list             - List peers");
        Console.WriteLine("  status           - Show NAT status");
        Console.WriteLine("  exit             - Exit");
        
        while (true)
        {
            Console.Write("> ");
            var cmd = Console.ReadLine()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cmd == null || cmd.Length == 0) continue;
            
            try
            {
                switch (cmd[0].ToLower())
                {
                    case "connect":
                        if (cmd.Length > 1)
                            await client.ConnectToPeerAsync(cmd[1]);
                        else
                            Console.WriteLine("Usage: connect <peer_name>");
                        break;
                        
                    case "send":
                        if (cmd.Length > 2)
                        {
                            var msg = string.Join(' ', cmd.Skip(2));
                            await client.SendP2PMessageAsync(cmd[1], msg);
                        }
                        else
                        {
                            Console.WriteLine("Usage: send <peer_name> <message>");
                        }
                        break;
                        
                    case "list":
                        Console.WriteLine(client.GetStatus());
                        break;
                        
                    case "status":
                        Console.WriteLine(client.GetStatus());
                        break;
                        
                    case "exit":
                        await client.CleanupAsync();
                        return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}