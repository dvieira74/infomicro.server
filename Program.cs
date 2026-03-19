using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Infomicro.Server
{
    class Program
    {
        // Thread-safe dictionary to store active hosts waiting for a connection
        private static ConcurrentDictionary<string, TcpClient> _activeHosts = new ConcurrentDictionary<string, TcpClient>();
        private static Random _random = new Random();

        static async Task Main(string[] args)
        {
            // Railway injects the PORT env variable automatically
            string portEnv = Environment.GetEnvironmentVariable("PORT");
            int port = int.TryParse(portEnv, out int p) ? p : 6666;

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Console.WriteLine($"[INFO] Infomicro Relay Server started on port {port}...");

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                Console.WriteLine($"[INFO] New connection from {client.Client.RemoteEndPoint}");
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        static async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                
                // Keep reading until we get a handshake message ending in \n
                byte[] buffer = new byte[256];
                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) return;

                string handshake = Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\0', '\r', '\n', ' ');
                Console.WriteLine($"[DEBUG] Handshake received: '{handshake}'");

                if (handshake == "HOST")
                {
                    // Generate a 6-digit distinct code
                    string code;
                    do
                    {
                        code = _random.Next(100000, 999999).ToString();
                    } while (_activeHosts.ContainsKey(code));

                    _activeHosts.TryAdd(code, client);
                    Console.WriteLine($"[HOST] Assigned code {code} to {client.Client.RemoteEndPoint}");

                    // Send the code to the host
                    byte[] response = Encoding.UTF8.GetBytes($"CODE|{code}\n");
                    await stream.WriteAsync(response, 0, response.Length);

                    // Await connection from a viewer. 
                    // The forwarding will be handled by the viewer's thread.
                    // Keep this thread alive to detect disconnection
                    byte[] dummy = new byte[1];
                    while (true)
                    {
                        if (client.Client.Poll(1000, SelectMode.SelectRead))
                        {
                            if (client.Client.Available == 0) break; // Client disconnected without Viewer
                            await Task.Delay(100);
                        }
                    }

                    // If we leave here, host disconnected before a viewer connects (or after Viewer disconnected)
                    _activeHosts.TryRemove(code, out _);
                    Console.WriteLine($"[HOST] Host {code} disconnected.");
                    client.Close();
                }
                else if (handshake.StartsWith("CONNECT|"))
                {
                    string targetCode = handshake.Split('|')[1];
                    Console.WriteLine($"[CLIENT] Requested connect to code {targetCode} from {client.Client.RemoteEndPoint}");

                    if (_activeHosts.TryRemove(targetCode, out TcpClient hostClient))
                    {
                        // Successfully matched!
                        Console.WriteLine($"[RELAY] Pairing {targetCode} Host {hostClient.Client.RemoteEndPoint} with Viewer {client.Client.RemoteEndPoint}");

                        // Tell the Viewer that connection is established
                        byte[] response = Encoding.UTF8.GetBytes("OK\n");
                        await stream.WriteAsync(response, 0, response.Length);

                        // Also send a "START" command to the Host so the host starts capturing
                        byte[] hostCmd = Encoding.UTF8.GetBytes("START\n");
                        await hostClient.GetStream().WriteAsync(hostCmd, 0, hostCmd.Length);

                        // Start bi-directional relay
                        var t1 = RelayDataAsync(hostClient.GetStream(), client.GetStream());
                        var t2 = RelayDataAsync(client.GetStream(), hostClient.GetStream());

                        await Task.WhenAny(t1, t2);

                        // When one stream disconnects, close both
                        Console.WriteLine($"[RELAY] Session {targetCode} ended.");
                        hostClient.Close();
                        client.Close();
                    }
                    else
                    {
                        Console.WriteLine($"[CLIENT] Code {targetCode} not found.");
                        byte[] err = Encoding.UTF8.GetBytes("NOT_FOUND\n");
                        await stream.WriteAsync(err, 0, err.Length);
                        client.Close();
                    }
                }
                else
                {
                    Console.WriteLine($"[WARN] Unknown handshake: {handshake}");
                    client.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {ex.Message}");
                client.Close();
            }
        }

        static async Task RelayDataAsync(NetworkStream source, NetworkStream destination)
        {
            try
            {
                byte[] buffer = new byte[81920]; 
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, bytesRead);
                }
            }
            catch
            {
                // Stream broken
            }
        }
    }
}
