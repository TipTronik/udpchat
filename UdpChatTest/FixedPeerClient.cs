// // ПРОБЛЕМА 1: Неправильный формат сообщений
// // Исправление: Убедимся, что формат сообщений одинаковый у всех клиентов
//
// using System.Net;
// using System.Net.Sockets;
// using System.Text;
// using System.Text.Json;
// using UdpHolePunching.Pure;
//
// public class FixedPeerClient : PurePeerClient
// {
//     public FixedPeerClient(string peerName, string rendezvousHost = "localhost", int rendezvousPort = 5555) 
//         : base(peerName, rendezvousHost, rendezvousPort)
//     {
//     }
//
//     protected override async Task ProcessMessageAsync(byte[] data, IPEndPoint sender)
//     {
//         string text = Encoding.UTF8.GetString(data);
//         Log($"RAW RECEIVED ({data.Length} bytes) from {sender}: {text}");
//         
//         // ПРОБЛЕМА 2: Возможно, сообщение приходит в другом формате
//         // Исправление: Пробуем разные форматы
//         
//         // Формат 1: Простой текст (P2P|peer|message)
//         if (text.StartsWith("P2P|"))
//         {
//             var parts = text.Split('|');
//             if (parts.Length >= 3)
//             {
//                 var fromPeer = parts[1];
//                 var message = string.Join("|", parts.Skip(2));
//                 
//                 Log($"✅ P2P message from {fromPeer}: {message}");
//                 OnMessageReceived?.Invoke(this, (fromPeer, message));
//                 
//                 // Обновляем информацию о пире
//                 lock (_lock)
//                 {
//                     if (_peers.TryGetValue(fromPeer, out var peer))
//                     {
//                         peer.DirectEndpoint = sender;
//                         peer.LastSeen = DateTime.UtcNow;
//                         Log($"✅ Updated endpoint for {fromPeer} to {sender}");
//                     }
//                     else
//                     {
//                         Log($"⚠️ Received message from unknown peer {fromPeer}");
//                     }
//                 }
//                 return;
//             }
//         }
//         
//         // Формат 2: JSON от сервера
//         try
//         {
//             var message = JsonSerializer.Deserialize<PeerMessage>(text);
//             if (message != null)
//             {
//                 Log($"✅ JSON message type {message.Type} from {message.Sender}");
//                 await HandleServerMessageAsync(message, sender);
//                 return;
//             }
//         }
//         catch (JsonException ex)
//         {
//             Log($"❌ JSON parse failed: {ex.Message}");
//         }
//         
//         // Формат 3: Hole punching пакеты
//         if (text.Contains("PUNCH") || text.Contains("SCAN"))
//         {
//             Log($"👊 Hole punch packet from {sender}: {text}");
//             
//             // Отвечаем на punch, чтобы установить соединение
//             if (text.StartsWith("PUNCH|"))
//             {
//                 var parts = text.Split('|');
//                 if (parts.Length >= 2)
//                 {
//                     var fromPeer = parts[1];
//                     Log($"👊 Responding to punch from {fromPeer}");
//                     
//                     byte[] response = Encoding.UTF8.GetBytes($"P2P|{_peerName}|PUNCH_ACK");
//                     await _udpClient.SendAsync(response, response.Length, sender);
//                 }
//             }
//             return;
//         }
//         
//         Log($"❓ Unknown message format from {sender}: {text}");
//     }
//     
//     // ПРОБЛЕМА 3: Hole punching может не создавать дырку в NAT
//     // Исправление: Усиленный hole punching
//     
//     protected override async Task PerformHolePunchingAsync(string targetPeerName)
//     {
//         PeerInfo? peer;
//         lock (_lock)
//         {
//             if (!_peers.TryGetValue(targetPeerName, out peer))
//             {
//                 Log($"❌ Cannot punch: peer {targetPeerName} not found");
//                 return;
//             }
//         }
//         
//         Log($"🔨 Starting ENHANCED hole punching to {targetPeerName} at {peer.ReportedEndpoint}");
//         
//         // Стратегия 1: Множественные пакеты с разных интервалов
//         Log("Strategy 1: Burst punching");
//         for (int burst = 0; burst < 3; burst++)
//         {
//             for (int i = 0; i < 20; i++)
//             {
//                 byte[] punchData = Encoding.UTF8.GetBytes($"PUNCH|{_peerName}|{burst}-{i}");
//                 await _udpClient.SendAsync(punchData, punchData.Length, peer.ReportedEndpoint!);
//                 
//                 // Отправляем также на альтернативные порты
//                 if (peer.ReportedEndpoint!.Port > 1024)
//                 {
//                     var altEndpoint = new IPEndPoint(peer.ReportedEndpoint.Address, 
//                         peer.ReportedEndpoint.Port + i % 5);
//                     await _udpClient.SendAsync(punchData, punchData.Length, altEndpoint);
//                 }
//                 
//                 await Task.Delay(10); // Быстрая стрельба
//             }
//             await Task.Delay(500); // Пауза между очередями
//         }
//         
//         // Стратегия 2: Одновременная отправка с разных локальных портов
//         Log("Strategy 2: Multi-port punching");
//         var tasks = new List<Task>();
//         for (int portOffset = 0; portOffset < 5; portOffset++)
//         {
//             int localPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port + portOffset;
//             tasks.Add(SendFromPortAsync(localPort, peer.ReportedEndpoint!, $"MULTI|{_peerName}"));
//         }
//         await Task.WhenAll(tasks);
//         
//         // Стратегия 3: Длительные пакеты (для некоторых NAT)
//         Log("Strategy 3: Long packets");
//         byte[] longData = Encoding.UTF8.GetBytes($"LONG_PUNCH|{_peerName}|" + new string('X', 1000));
//         for (int i = 0; i < 5; i++)
//         {
//             await _udpClient.SendAsync(longData, longData.Length, peer.ReportedEndpoint!);
//             await Task.Delay(200);
//         }
//         
//         Log($"✅ Hole punching complete for {targetPeerName}");
//     }
//     
//     private async Task SendFromPortAsync(int localPort, IPEndPoint target, string message)
//     {
//         try
//         {
//             using var client = new UdpClient(localPort);
//             client.Client.ReceiveTimeout = 100;
//             byte[] data = Encoding.UTF8.GetBytes(message);
//             await client.SendAsync(data, data.Length, target);
//             Log($"  Sent from port {localPort} to {target}");
//         }
//         catch (Exception ex)
//         {
//             Log($"  Port {localPort} failed: {ex.Message}");
//         }
//     }
// }