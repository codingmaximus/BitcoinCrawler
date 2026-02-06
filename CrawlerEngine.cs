using BitcoinCrawlerStats.Models;
using OnixLabs.Core.Linq;
using SocksSharp.Proxy;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using static BitcoinCrawlerStats.StringUtils;

namespace BitcoinCrawlerStats
{
    public class CrawlerEngine
    {
        // Bitcoin mainnet magic bytes
        private static readonly uint Magic = 0xD9B4BEF9;

        // Protocol version we advertise (current as of late 2025 ~70016 or higher; 70015 is safe)
        private const int ProtocolVersion = 70016;

        // Services we advertise (NODE_NETWORK)
        private static readonly ulong Services = 1;

        // Bitcoin mainnet port
        public const int BitcoinPort = 8333;

        public const int MAX_KEPT_BLOCKS = 1000;
        public const int MAX_TOR_SIMULTANEOUS_CONNECT = 10;
        public const int MAX_ACTIVE_SESSIONS = 200;
        public const int MAX_VISIBLE_LOG_ENTRIES = 15;

        // Hardcoded DNS seeds (updated from Bitcoin Core as of 2025; some may change)
        private static readonly string[] DnsSeeds = {
            "seed.bitcoin.sipa.be",
            "dnsseed.bluematt.me",
            "dnsseed.bitcoin.dashjr-list-of-p2p-nodes.us.",
            "seed.bitcoin.haf.ovh.",
            "seed.bitcoin.jonasschnelli.ch.",
            "seed.btc.petertodd.net.",
            "seed.bitcoin.sprovoost.nl.",
            "dnsseed.emzy.de.",
            "seed.bitcoin.wiz.biz.",
            "seed.mainnet.achownodes.xyz."
        };

        // Statistics collections
        internal readonly ConcurrentDictionary<string, int> UserAgentStats = new ConcurrentDictionary<string, int>();           // user agent -> count

        internal readonly ConcurrentDictionary<string, int> ActiveUserAgentStats = new ConcurrentDictionary<string, int>();     // user agent -> count
        internal readonly ConcurrentDictionary<string, int> InactiveUserAgentStats = new ConcurrentDictionary<string, int>();   // user agent -> count
        internal readonly ConcurrentDictionary<string, int> SpammerUserAgentStats = new ConcurrentDictionary<string, int>();    // user agent -> count

        internal readonly ConcurrentDictionary<string, int> ProtocolStats = new ConcurrentDictionary<string, int>(); // Successful connections only

        // Initial peers from seeds (IP:port)
        internal readonly ConcurrentBag<(IPAddress Ip, int Port)> InitialPeers = new ConcurrentBag<(IPAddress, int Port)>();

        // Visited to avoid re-crawling
        internal readonly ConcurrentHashSet<string> Collected = new ConcurrentHashSet<string>();    // host:port. Collected from addr messages
        internal readonly ConcurrentDictionary<string, PeerInfo> Unvisited = new ConcurrentDictionary<string, PeerInfo>();  // host:port -> PeerInfo
        internal readonly ConcurrentHashSet<string> Visited = new ConcurrentHashSet<string>();      // host:port. Connected successfully or not
        internal readonly ConcurrentHashSet<string> Evaluated = new ConcurrentHashSet<string>();    // host:port. Really tested or unable to connect

        internal readonly ConcurrentDictionary<string, BlockInfo> BlocksAnnounced = new ConcurrentDictionary<string, BlockInfo>(); // Hash -> BlockInfo

        internal ConcurrentDictionary<Guid, SessionInfo> Sessions = new ConcurrentDictionary<Guid, SessionInfo>();
        internal readonly ConcurrentDictionary<string, SessionHistory> AllSessionHistory = new ConcurrentDictionary<string, SessionHistory>();  // host:port -> SessionHistory

        internal static FixedFifoQueue<String> LogQueue = new FixedFifoQueue<String>(MAX_VISIBLE_LOG_ENTRIES);

        internal LiveStatistics LiveStatistics => _liveStatistics;
        internal ConsoleRenderer? Renderer => _renderer;
        public CrawlerCommandLineSettings Settings => _settings;

        // Max peers to crawl (limit to avoid overwhelming the network or your machine)
        //private const int MaxPeersToCrawl = 5000;

        private ProxyClient<Socks5>? _classProxyClient;

        LiveStatistics _liveStatistics = new LiveStatistics();

        int _torConnectCount = 0;
        object _connectLock = new object();

        readonly CommandContext _context;
        readonly CrawlerCommandLineSettings _settings;
        readonly CancellationToken _cancellationToken;
        
        readonly CrawlerPersistence _persistence;
        Stopwatch? _stopwatch;

        ConsoleRenderer? _renderer;

        public CrawlerEngine(CommandContext context, CrawlerCommandLineSettings settings, CancellationToken cancellationToken)
        {
            _context = context;
            _settings = settings;
            _cancellationToken = cancellationToken; // CrawlerCommand will trigger this when CTRL+C is pressed

            _persistence = new CrawlerPersistence(this);
        }

        public async Task<int> ExecuteAsync()
        {
            Console.WriteLine("Bitcoin P2P User-Agent Crawler starting...");
            //Console.WriteLine($"Target max peers: {MaxPeersToCrawl}");

            if (!(await _persistence.StartAsync()))
                return 1;

            await _persistence.LoadFromDbAsync();

            if (this.Unvisited.Count != 0)
            {
                int nullIp = 0;
                foreach (var item in this.Unvisited)
                {
                    var peer = item.Value;
                    if ((item.Value.NetworkId == (int)NetworkId.IPv4 || peer.NetworkId == (int)NetworkId.IPv6)
                        && peer.IP == null)
                    {
                        nullIp++;
                        continue;
                    }

                    AddToCollectedIfNew(PeerToString(item.Value));
                }

                if (nullIp > 0)
                    MyLog($"WARNING: found {nullIp} unvisited entries with NULL IP address");
            }

            if (this.Evaluated.Count != 0)
                foreach (var item in this.Evaluated)
                {
                    AddToCollectedIfNew(item);
                    Visited.Add(item);
                }

#if DEBUG
            //var existingKey = this.UserAgentStats.Take(1).FirstOrDefault().Key;
            //if (!String.IsNullOrEmpty(existingKey))
            //{
            //    this.ActiveUserAgentStats[existingKey] = 1;
            //    await _persistence.SaveToDbAsync();
            //}
#endif //DEBUG

            Console.WriteLine("Press Ctrl+Break to stop...");

            // Use SOCKS5 proxy for .onion
            var proxySettings = new ProxySettings
            {
                Host = _settings.TorProxyHost,
                Port = _settings.TorProxyPort
            };

            _classProxyClient = new ProxyClient<Socks5>();
            _classProxyClient.Settings = proxySettings;

            _stopwatch = Stopwatch.StartNew();

            _renderer = new ConsoleRenderer(this, _stopwatch, _settings, _cancellationToken );
            _renderer.Start();

            if (this.Unvisited.Count == 0)
            {
                if (!String.IsNullOrEmpty(_settings.SingleSeedHost))
                {
                    var addresses = await Dns.GetHostAddressesAsync(_settings.SingleSeedHost);
                    var ip = addresses.FirstOrDefault();
                    if (ip == null)
                        throw new ArgumentException("Unable to resolve single seed hostname");

                    Console.WriteLine($"Using single seed: {_settings.SingleSeedHost}:{_settings.SingleSeedPort}");
                    AddInitialPeerIfNew(ip, _settings.SingleSeedPort);
                }
                else
                {
                    // Step 1: Resolve initial peers from DNS seeds
                    await DiscoverInitialPeers();
                }
            }

            // Step 2: Crawl peers recursively (BFS style).
            // Will block until user presses CTRL+C
            await CrawlPeers();

            // Step 3: Output statistics
            PrintStatistics();

            var sessions = this.Sessions.Select(p => p.Value).ToList();
            foreach (var si in sessions)
                si.CancellationTokenSource.Cancel();

            var taskList = sessions.Where(p => p.Task != null).Select(p => p.Task);
            if (taskList.Any())
            {
                Console.WriteLine("");
                Console.WriteLine("Waiting for all sessions to finish... ");
                if (!Task.WaitAll(taskList.ToArray()!, TimeSpan.FromSeconds(30)))
                    Console.WriteLine("WARNING: timeout waiting for sessions to finish");
                else
                    Console.WriteLine("Done!");
            }

            _renderer.PrintLiveStatistics();

            try
            {
                Console.WriteLine("");
                Console.WriteLine("Saving data...");
                _persistence?.SaveToDbAsync().Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving data: {ex.Message}");
            }

            Console.WriteLine("Crawling completed.");

            return 0;
        }


        private async Task DiscoverInitialPeers()
        {
            Console.WriteLine("Resolving initial peers from DNS seeds...");
            var tasks = DnsSeeds.Select(async seed =>
            {
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(seed);
                    foreach (var ip in addresses)
                    {
                        AddInitialPeerIfNew(ip, BitcoinPort);
                    }
                }
                catch { /* Ignore failed seed */ }
            });

            await Task.WhenAll(tasks);
            Console.WriteLine($"Added {InitialPeers.Count} initial peers from DNS seeds.");
        }

        private async Task CrawlPeers()
        {
            var sb = new StringBuilder("Starting recursive crawl");
            var sbExtra = new StringBuilder();
            if (!_settings.DisableIP)
            {
                sbExtra.Append(" using protocols: ");
                sbExtra.Append("IP");
            }
            if (!_settings.DisableTor)
            {
                if (sbExtra.Length == 0)
                    sbExtra.Append(" using protocols: ");
                else
                    sbExtra.Append(" and ");
                sbExtra.Append("Tor");
            }

            Console.WriteLine(sb.ToString() + sbExtra.ToString());

            if (_settings.EnableHttpServer)
                _ = RunHttpServerAsync(_settings.HttpServerAddress!);

            // Sessions' housekeeping loop
            _ = Task.Run(() =>
            {
                while (!_cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        SessionsHouseKeeping();
                    }
                    catch (Exception ex)
                    {
                        MyLog($"SessionHouseKeeping error: {ex.Message}");
                    }
                    finally
                    {
                        Task.Delay(10000).Wait();
                    }
                }
            });

            // Connector loop
            _ = Task.Run(() =>
            {
                while (!_cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        ConnectToNewPeers(_settings.MaxActiveSessions);
                    }
                    catch (Exception ex)
                    {
                        MyLog($"ConnectToNewPeers error: {ex.Message}");
                    }
                    finally
                    {
                        Task.Delay(5000).Wait();
                    }
                }
            });

            // Persistence loop
            _ = Task.Run(() =>
            {
                Task.Delay(10000).Wait();
                while (!_cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        _persistence.SaveToDbAsync().Wait();
                    }
                    catch (Exception ex)
                    {
                        MyLog($"Persistence error: {ex.Message}");
                    }
                    finally
                    {
                        Task.Delay(30000).Wait();
                    }
                }
            });

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 50 };

            // connect to all initial peers
            Parallel.ForEach(InitialPeers.ToArray(), parallelOptions, peer =>
            {
                if (_cancellationToken.IsCancellationRequested) return;

                // Assuming these are all IPv4 or IPv6 addresses
                ThrottledConnect(new PeerInfo { NetworkId = (int)NetworkId.IPv4, IP = peer.Ip, Port = peer.Port} );
            });

            // Keep running until limit reached
            while (!_cancellationToken.IsCancellationRequested /*&& InitialPeers.Count > Visited.Count*/)
            {
                await Task.Delay(1000);
            }
        }

        private byte[] BuildVersionPayload()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(ProtocolVersion);                 // version
            writer.Write(Services);                        // services
            writer.Write((long)DateTime.UtcNow.ToBinary()); // timestamp

            // recv addr (empty)
            writer.Write((ulong)0); // services
            writer.Write(new byte[16]); // IPv6 (zero)
            writer.Write((ushort)0); // port

            // our addr (localhost)
            writer.Write(Services);
            writer.Write(new byte[16]);
            writer.Write((ushort)0);

            writer.Write((ulong)new Random().NextInt64()); // nonce
            writer.WriteVarString(_settings.UserAgent!);
            writer.Write(0); // start_height
            writer.Write(false); // relay

            return ms.ToArray();
        }

        private static byte[] BuildHeadersPayload(int count)
        {
            return new byte[] { 0x00 };
        }

        private static byte[] BuildNoncePayload()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write((ulong)new Random().NextInt64()); // nonce

            return ms.ToArray();
        }

        private static byte[] BuildSendCmpctPayload()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write((byte)1);      // announce
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, 1); // version
            writer.Write(buffer);

            return ms.ToArray();
        }

        private static byte[] BuildMessage(string command, byte[] payload)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(Magic);
            writer.Write(Encoding.ASCII.GetBytes(command.PadRight(12, '\0')));
            writer.Write((uint)payload.Length);
            writer.Write(ComputeChecksum(payload));
            writer.Write(payload);

            return ms.ToArray();
        }

        private static uint ComputeChecksum(byte[] payload)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Security.Cryptography.SHA256.HashData(payload));
            return BitConverter.ToUInt32(hash, 0);
        }

        private async Task ConnectAndProcessPeerAsync(PeerInfo peer, string key, SessionInfo sessionInfo)
        {
            TcpClient? client = null;
            NetworkStream? stream = null;

            int networkId = peer.NetworkId;
            bool isOnion = (networkId == (int)NetworkId.Tor || networkId == (int)NetworkId.TorV3);
            try
            {
                var cts = sessionInfo.CancellationTokenSource;
                try
                {
                    if (isOnion)
                    {
                        try
                        {
                            Interlocked.Increment(ref _torConnectCount);
                            stream = _classProxyClient!.GetDestinationStream(peer.Host, peer.Port); // .onion resolves via Tor
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _torConnectCount);
                        }

                        //Console.WriteLine("Onion connect SUCCESS!!!");
                        Interlocked.Increment(ref _liveStatistics.TorSuccess);
                    }
                    else if (networkId == (int)NetworkId.IPv4 || networkId == (int)NetworkId.IPv6)
                    {
                        client = new TcpClient(peer.IP!.AddressFamily); // Use correct family
                        await client.ConnectAsync(peer.IP, peer.Port, cts.Token);
                        stream = client.GetStream();
                    }
                    else
                        return; // Not supported
                }
                catch (Exception cex)
                {
                    // Connection failed – normal in P2P
                    if (isOnion)
                    {
                        if (_settings.Verbose)
                            Console.WriteLine("Onion connect ERROR: " + cex.Message);
                        Interlocked.Increment(ref _liveStatistics.TorErrors);
                    }
                    else
                    {
                        if (_settings.Verbose)
                            Console.WriteLine("TCP/IP connect ERROR: " + cex.Message);
                    }

                    Interlocked.Increment(ref _liveStatistics.ConnectionErrors);
                    sessionInfo.SessionHistory.Connected = false;
                    sessionInfo.SessionHistory.ConnectionError = !String.IsNullOrEmpty(cex.Message) ? cex.Message : cex.GetType().Name;
                    throw;
                }

                StateHasChanged();  // Signal UI to refresh
                sessionInfo.SessionHistory.Connected = true;

                await ProcessPeerStreamAsync(stream, sessionInfo, peer);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException || ex is TaskCanceledException)
                {
                    // Program is shutting down
                    // Don't persist...
                    sessionInfo.SessionHistory.Ignore = true;
                    // We don't want to do anything else, not even add peer to the Evaluated list...
                    return;
                }

                if (stream != null)
                {
                    Interlocked.Increment(ref _liveStatistics.StreamErrors);
                    sessionInfo.SessionHistory.StreamError = ex.Message;
                }
                MarkAsEvaluated(key, sessionInfo);   // Keep it in the evaluated set, so we don't connect to it again...
                StateHasChanged();  // Signal UI to refresh
            }
            finally
            {
                stream?.Close();
                client?.Close();
                if (sessionInfo != null)
                {
                    sessionInfo.MessageBuffer?.Dispose();
                    Sessions.Remove(sessionInfo.Id, out _);
                    StateHasChanged();  // Signal UI to refresh
                }
            }
        }

        private async Task ProcessPeerStreamAsync(NetworkStream stream, SessionInfo sessionInfo, PeerInfo peerInfo, bool pinned = false)
        {
            var contextStr = peerInfo.Host ?? peerInfo.IP?.ToString()!;
            MemoryStream? msInner = null;
            try
            {
                ProtocolStats.AddOrUpdate(sessionInfo.NetworkId.ToString(), 1, (_, c) => c + 1);

                var cts = sessionInfo.CancellationTokenSource;

                var versionPayload = BuildVersionPayload();
                var versionMessage = BuildMessage("version", versionPayload);
                await stream.WriteAsync(versionMessage, cts.Token);
                await stream.FlushAsync(cts.Token);
                if (_settings.Verbose)
                    Console.WriteLine($"{contextStr}: SENT version");

                const int MAX_MESSAGE_SIZE = 65536; /* using 0x2000000 would be too large */

                var buffer = new byte[MAX_MESSAGE_SIZE];

                sessionInfo.MessageBuffer = new MemoryStream();

                var lastAddrRecv = DateTime.Now;

                while (/*client.Connected &&*/ !cts.IsCancellationRequested)
                {
                    if (DateTime.Now - lastAddrRecv > TimeSpan.FromMinutes(60))
                        // No more gossip? Exit...
                        break;

                    //int bytesRead = await stream.ReadAsync(buffer, 0, MAX_MESSAGE_SIZE, cts.Token);
                    int bytesRead = await stream.ReadAsync(buffer, cts.Token);
                    if (bytesRead == 0) break;

                    sessionInfo.MessageBuffer.Position = sessionInfo.MessageBuffer.Length;
                    sessionInfo.MessageBuffer.Write(buffer, 0, bytesRead);
                    sessionInfo.MessageBuffer.Flush();

                    msInner = new MemoryStream(sessionInfo.MessageBuffer.ToArray());
                    msInner.Flush();

                    while (TryParseMessage(msInner, sessionInfo, out var command, out var payload))
                    {
                        if (command != "ping")
                        {
                            sessionInfo.LastReceive = DateTime.Now;
                            sessionInfo.LastMessage = command;
                        }

                        if (command == "version")
                        {
                            if (_settings.Verbose)
                                Console.WriteLine($"{contextStr}: GOT version");
                            var userAgent = ParseUserAgent(payload);
                            if (!string.IsNullOrEmpty(userAgent))
                            {
                                sessionInfo.UserAgent = userAgent;
                                sessionInfo.SessionHistory.UserAgent = userAgent;
                                UserAgentStats.AddOrUpdate(userAgent, 1, (_, c) => c + 1);
                            }

                            var sendaddrv2Message = BuildMessage("sendaddrv2", Array.Empty<byte>());
                            await stream.WriteAsync(sendaddrv2Message, cts.Token);
                            await stream.FlushAsync(cts.Token);
                            if (_settings.Verbose)
                                Console.WriteLine($"{contextStr}: SENT sendaddrv2");
                        }
                        else if (command == "sendaddrv2")
                        {
                            if (_settings.Verbose)
                                Console.WriteLine($"{contextStr}: GOT sendaddrv2");
                            //if (payload == null || payload.Length == 0)
                            //{
                            //    var verack = BuildMessage("verack", Array.Empty<byte>());
                            //    await stream.WriteAsync(verack, cts.Token);
                            //    //Console.WriteLine($"{contextStr}: SENT verack");
                            //}
                        }
                        else if (command == "ping")
                        {
                            // Respond with pong (nonce is first 8 bytes of payload)
                            byte[] nonce = new byte[8];
                            Array.Copy(payload, nonce, 8);
                            byte[] pong = BuildMessage("pong", nonce);
                            await stream.WriteAsync(pong, cts.Token);
                            //Console.WriteLine("Sent: pong");
                        }
                        else if (command == "pong")
                        {
                            if (_settings.Verbose)
                                Console.WriteLine($"{contextStr}: GOT pong");
                        }
                        else if (command == "verack")
                        {
                            sessionInfo.HandshakeComplete();

                            if (_settings.Verbose)
                                Console.WriteLine($"{contextStr}: GOT verack");

                            var verack = BuildMessage("verack", Array.Empty<byte>());
                            await stream.WriteAsync(verack, cts.Token);
                            if (_settings.Verbose)
                                Console.WriteLine($"{contextStr}: SENT verack");

                            var ping = BuildMessage("ping", BuildNoncePayload());
                            await stream.WriteAsync(ping, cts.Token);
                            if (_settings.Verbose)
                                Console.WriteLine($"{contextStr}: SENT ping");

                            var sendheaders = BuildMessage("sendheaders", Array.Empty<byte>());
                            await stream.WriteAsync(sendheaders, cts.Token);
                            await stream.FlushAsync(cts.Token);
                            if (_settings.Verbose)
                                Console.WriteLine($"{contextStr}: SENT sendheaders");

                            var sendcmpct = BuildMessage("sendcmpct", BuildSendCmpctPayload());
                            await stream.WriteAsync(sendcmpct, cts.Token);
                            await stream.FlushAsync(cts.Token);
                            if (_settings.Verbose)
                                Console.WriteLine($"{contextStr}: SENT sendcmpct");

                            var getaddr = BuildMessage("getaddr", Array.Empty<byte>());
                            await stream.WriteAsync(getaddr, cts.Token);
                            await stream.FlushAsync(cts.Token);
                            if (_settings.Verbose)
                                Console.WriteLine($"{contextStr}: SENT getaddr");

                        }
                        else if (command == "addr" && payload.Length > 0)
                        {
                            lastAddrRecv = DateTime.Now;
                            Interlocked.Increment(ref sessionInfo.AddrMessagesRcvd);

                            //Console.WriteLine($"{contextStr}: GOT addr");

                            var newPeers = ParseAddr(payload);
                            foreach ((IPAddress ip, int port) peer in newPeers)
                            {
                                if (!_settings.DisableIP)
                                {
                                    Interlocked.Increment(ref sessionInfo.Addresses);

                                    var networkId = (int)NetworkId.IPv4;
                                    if (peer.ip.AddressFamily == AddressFamily.InterNetworkV6)
                                        networkId = (int)NetworkId.IPv6;

                                    var newPeerInfo = new PeerInfo((networkId, "", peer.ip, peer.port));
                                    string key = PeerToString(newPeerInfo);
                                    AddToCollectedIfNew(key);
                                    AddToUnvisitedIfNew(key, newPeerInfo);
                                }
                            }
                        }
                        else if (command == "addrv2" && payload.Length > 0)
                        {
                            //Console.WriteLine($"{contextStr}: GOT addrv2");

                            try
                            {
                                lastAddrRecv = DateTime.Now;
                                Interlocked.Increment(ref sessionInfo.AddrMessagesRcvd);

                                var addresses = ExtractAddressesFromAddrv2(payload);

                                var stats = new Dictionary<int, int>
                                {
                                    { (int)NetworkId.IPv4, 0 },
                                    { (int)NetworkId.IPv6, 0 },
                                    { (int)NetworkId.Tor, 0 },
                                    { (int)NetworkId.TorV3, 0 },
                                    { (int)NetworkId.i2p, 0 }
                                };

                                foreach (var address in addresses)
                                {
                                    int current;
                                    if (stats.TryGetValue(address.networkId, out current))
                                        stats[address.networkId] = current + 1;
                                    else
                                        stats[address.networkId] = 1;

                                    //Console.WriteLine($"Session [{SafeSubstring(sessionInfo.Id.ToString(),0,8)}] now has {sessionInfo.Addresses} addresses");

                                    if (_settings.Verbose)
                                    {
                                        if (address.networkId == (int)NetworkId.IPv4 || address.networkId == (int)NetworkId.IPv6)
                                            Console.WriteLine($"\t\t{address.ip.ToString()}:{address.port}");
                                        else if (address.networkId == (int)NetworkId.Tor || address.networkId == (int)NetworkId.TorV3)
                                            Console.WriteLine($"\t\t{address.host}:{address.port}");
                                    }
                                }

                                if (_settings.Verbose)
                                {
                                    Console.WriteLine($"\tADDRV2: totals: IPv4: {stats[(int)NetworkId.IPv4]}  IPv6: {stats[(int)NetworkId.IPv6]}  Tor: {stats[(int)NetworkId.TorV3]}");

                                    //var getaddr = BuildMessage("getaddr", Array.Empty<byte>());
                                    //await stream.WriteAsync(getaddr, cts.Token);
                                    //Console.WriteLine($"{contextStr}: SENT getaddr");
                                }

                                if (!_settings.DisableTor)
                                {
                                    var torAddresses = addresses.Where(p => p.networkId == (int)NetworkId.TorV3).ToList();  // Tor v3
                                    foreach (var torAddr in torAddresses)
                                    {
                                        Interlocked.Increment(ref sessionInfo.Addresses);

                                        var newPeerInfo = new PeerInfo(((int)NetworkId.TorV3, torAddr.host, null!, torAddr.port));
                                        var key = PeerToString(newPeerInfo);
                                        AddToCollectedIfNew(key);
                                        AddToUnvisitedIfNew(key, newPeerInfo);
                                    }
                                }

                                if (!_settings.DisableIP)
                                {
                                    var ipAddresses = addresses.Where(p => p.networkId == (int)NetworkId.IPv4 || p.networkId == (int)NetworkId.IPv6).ToList();
                                    foreach (var peer in ipAddresses)
                                    {
                                        Interlocked.Increment(ref sessionInfo.Addresses);

                                        var newPeerInfo = new PeerInfo(peer);
                                        var key = PeerToString(newPeerInfo);
                                        AddToCollectedIfNew(key);
                                        AddToUnvisitedIfNew(key, newPeerInfo);
                                    }
                                }
                            }
                            catch
                            {
                                if (_settings.Verbose)
                                    Console.WriteLine($"{contextStr}: Error extracting addresses from addrv2");
                            }
                        }
                        else if (command == "inv" && payload.Length > 0)
                        {
                            //Console.WriteLine($"{contextStr}: Got inv message");

                            ParseInvMessage(payload, sessionInfo, contextStr);
                        }
                        else if (command == "cmpctblock" && payload.Length > 0)
                        {
                            Console.WriteLine($"{contextStr}: Got cmpctblock message");
                        }
                        else if (command == "headers" && payload.Length > 0)
                        {
                            Console.WriteLine($"{contextStr}: Got headers message");
                        }
                        //else if (command == "getheaders")
                        //{
                        //    var hdrspayload = BuildHeadersPayload(0);
                        //    var headersMessage = BuildMessage("headers", hdrspayload);
                        //
                        //    await stream.WriteAsync(headersMessage, cts.Token);
                        //    await stream.FlushAsync(cts.Token);
                        //}
                        else if (command != "feefilter" && command != "sendcmpct"
                            && command != "sendheaders" && command != "wtxidrelay"
                            && command != "getheaders"
                            && _settings.Verbose)
                            Console.WriteLine($"{contextStr}: Got '{command}' message");

                        // Preserve unparsed remainder
                        var remaining = sessionInfo.MessageBuffer.ToArray().Skip((int)msInner.Position).ToArray();
                        if (sessionInfo.MessageBuffer != null)
                            sessionInfo.MessageBuffer.Dispose();
                        sessionInfo.MessageBuffer = new MemoryStream();
                        if (remaining.Length > 0)
                        {
                            sessionInfo.MessageBuffer.Write(remaining);
                            sessionInfo.MessageBuffer.Flush();
                        }
                        sessionInfo.MessageBuffer.Position = 0;
                        if (msInner != null)
                            msInner.Dispose();
                        msInner = new MemoryStream(sessionInfo.MessageBuffer.ToArray());
                    }
                } // while

                sessionInfo.SessionHistory.LoopFinished = true;
            }
            finally
            {
                msInner?.Dispose();
            }
        }

        bool TryParseMessage(MemoryStream stream, SessionInfo sessionInfo, out string command, out byte[] payload)
        {
            var saved = stream.Position;
            var ret = DoTryParseMessage(stream, sessionInfo, out command, out payload);
            if (!ret)
            {
                // Rewind and try again later...
                stream.Position = saved;
            }
            return ret;
        }

        private bool DoTryParseMessage(MemoryStream stream, SessionInfo sessionInfo, out string command, out byte[] payload)
        {
            command = null!;
            payload = null!;

            var context = sessionInfo.UserAgent != null ? SafeSubstring(sessionInfo.UserAgent, Math.Max(sessionInfo.UserAgent.Length - 30, 0)) : "";
            if (stream.Length - stream.Position < 24)
            {
                if (_settings.DebugParse && stream.Length - stream.Position != 0)
                    MyLog($"{context}: PRS: near the end of buffer ({stream.Length - stream.Position} bytes), returning false");
                return false;
            }

            using var reader = new BinaryReader(stream, Encoding.ASCII, true);

            var magic = reader.ReadUInt32();
            if (magic != Magic) return false;

            var cmdBytes = reader.ReadBytes(12);
            var cmd = Encoding.ASCII.GetString(cmdBytes.TakeWhile(b => b != 0).ToArray());

            var length = reader.ReadUInt32();
            var checksum = reader.ReadUInt32();

            if (stream.Length - stream.Position < length)
            {
                sessionInfo.WantedLength = length;
                if (_settings.DebugParse && length > 10000)
                    MyLog($"{context}: PRS: still not enough ({stream.Length} - {stream.Position} < {length}), returning false");
                return false;
            }

            var pay = reader.ReadBytes((int)length);
            //if (BitConverter.ToUInt32(ComputeChecksum(pay), 0) != checksum) return false;
            if (ComputeChecksum(pay) != checksum)
            {
                if (_settings.DebugParse)
                    MyLog($"{context}: PRS: Wrong checksum, returning false");
                return false;
            }

            command = cmd;
            payload = pay;
            //MyLog($"{context}: PRS: '{cmd}' message parsed!");
            sessionInfo.WantedLength = 0;
            return true;
        }

        private static string ParseUserAgent(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms);

            reader.ReadInt32();                 // version
            reader.ReadUInt64();                // services
            reader.ReadInt64();                 // timestamp

            // addr_recv
            reader.ReadUInt64();                // services
            reader.ReadBytes(16);                // IP
            reader.ReadInt16();                 // port (but ignore value)

            // addr_from
            reader.ReadUInt64();                // services
            reader.ReadBytes(16);                // IP
            reader.ReadInt16();                 // port (ignore)

            reader.ReadUInt64();                // nonce

            var userAgent = reader.ReadVarString();

            // Optional: consume remaining fields if present (start_height and relay)
            // But safe to ignore for our purpose, as we only need user_agent

            return userAgent;
        }

        void ParseInvMessage(byte[] payload, SessionInfo sessionInfo, string context)
        {
            try
            {
                using var ms = new MemoryStream(payload);
                using var reader = new BinaryReader(ms);

                ulong count = reader.ReadVarint();

                for (ulong i = 0; i < count; i++)
                {
                    uint type = reader.ReadUInt32();
                    var hash = reader.ReadBytes(32);

                    if (type == 2) //MSG_BLOCK
                    {
                        var hashStr = HashToString(hash);
                        if (_settings.Verbose)
                            Console.WriteLine($"{context}: *** MSG_BLOCK hash = {hashStr}");

                        if (!string.IsNullOrEmpty(hashStr))
                        {
                            BlocksAnnounced.AddOrUpdate(
                                    hashStr,
                                    new BlockInfo { Count = 1, FirstSeen = DateTime.Now, LastReceived = DateTime.Now, AtSessionCount = Sessions.Count },  // Keep track of the current session count
                                    (_, c) => new BlockInfo { Count = c.Count + 1, LastReceived = DateTime.Now, AtSessionCount = c.AtSessionCount, FirstSeen = c.FirstSeen }
                                );

                            sessionInfo.BlocksAnnounced[hashStr] = DateTime.Now;

                            BlocksAnnouncedHouseKeeping();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ParseInvMessage: ERROR: " + ex.Message);
            }
        }

        private static List<(IPAddress, int)> ParseAddr(byte[] payload)
        {
            var peers = new List<(IPAddress, int)>();
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms);

            var count = reader.ReadVarint();
            for (ulong i = 0; i < count; i++)
            {
                // Time field present if message from node with version >= 31402
                if (ms.Position + 30 <= ms.Length)
                {
                    reader.ReadUInt32(); // time
                }

                reader.ReadUInt64(); // services
                var ipBytes = reader.ReadBytes(16);
                var port = IPAddress.NetworkToHostOrder(reader.ReadInt16());

                var ip = new IPAddress(ipBytes);

                // Map IPv4-mapped to IPv4 for cleaner connections
                if (ip.IsIPv4MappedToIPv6)
                {
                    ip = ip.MapToIPv4();
                }

                peers.Add((ip, port));
            }
            return peers;
        }

        private void PrintStatistics()
        {
            Console.WriteLine("\n=== User Agent Statistics ===");
            var total = UserAgentStats.Values.Sum();
            Console.WriteLine($"Total unique peers connected: {total}  (IPv4 + IPv6 + Onion)");

            var sorted = UserAgentStats.OrderByDescending(kv => kv.Value);
            foreach (var kv in sorted.Take(20)) // Top 20
            {
                Console.WriteLine($"{kv.Key}: {kv.Value} ({(kv.Value * 100.0 / total):F2}%)");
            }

            if (sorted.Count() > 20)
            {
                Console.WriteLine("... (and others)");
            }
        }

        private void AddInitialPeerIfNew(IPAddress ip, int port)
        {
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();

            InitialPeers.Add((ip, port));
        }

        private static string PeerToString(IPAddress ip, int port)
        {
            return (ip != null && ip.AddressFamily == AddressFamily.InterNetworkV6)
                ? $"[{ip}]:{port}"
                : $"{ip}:{port}";
        }

        private static string PeerToString(PeerInfo peer)
        {
            if (peer.NetworkId == (int)NetworkId.IPv4 || peer.NetworkId == (int)NetworkId.IPv6)
                return PeerToString(peer.IP!, peer.Port);

            return $"{peer.Host}:{peer.Port}";
        }

        List<(int networkId, string host, IPAddress ip, int port)> ExtractAddressesFromAddrv2(byte[] payload)
        {
            var ret = new List<(int, string, IPAddress, int)>();

            if (payload == null || payload.Length == 0)
                return ret;

            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms);

            // Read the count (VarInt / CompactSize)
            var count = reader.ReadVarint();

            for (uint i = 0; i < count; i++)
            {
                try
                {
                    // addrv2 format (BIP155)
                    var time = reader.ReadUInt32();
                    //var services = reader.ReadCompactSize(); // services as CompactSize
                    var services = reader.ReadVarint();
                    var networkId = (byte)reader.ReadByte(); // 1-byte network ID

                    var addrLength = reader.ReadVarint();
                    if (networkId == 0x04 && addrLength != 32) // Tor v3 must be exactly 32 bytes (ed25519 pubkey)
                        continue;

                    if (addrLength > 512)
                        // reject
                        continue;

                    string? host = null;
                    short port = 0;
                    IPAddress ip = null!;
                    if (networkId == 0x01)
                    {
                        // IPV4

                        var ipBytes = reader.ReadBytes(4);
                        port = IPAddress.NetworkToHostOrder(reader.ReadInt16());

                        ip = new IPAddress(ipBytes);
                        host = ip.ToString();
                    }
                    else if (networkId == 0x02)
                    {
                        // IPV6

                        var ipBytes = reader.ReadBytes(16);
                        port = IPAddress.NetworkToHostOrder(reader.ReadInt16());

                        ip = new IPAddress(ipBytes);

                        // Map IPv4-mapped to IPv4 for cleaner connections
                        if (ip.IsIPv4MappedToIPv6)
                            ip = ip.MapToIPv4();

                        host = ip.ToString();
                    }
                    else if (networkId == 0x04)
                    {
                        // TOR V3

                        var pubkeyBytes = reader.ReadBytes(32);
                        //var port = stream.BigEndianReadUInt16();
                        port = (short)((reader.ReadByte() << 8) | reader.ReadByte());

                        // Convert to .onion string using Tor's standard encoding
                        host = PubkeyToOnionAddress(pubkeyBytes);
                    }
                    else
                    {
                        if (_settings.Verbose)
                            Console.WriteLine($"Addrv2: got unsupported networkId {(NetworkId)networkId}");
                    }

                    if (!string.IsNullOrEmpty(host))
                        ret.Add((networkId, host, ip, port));
                }
                catch (Exception ex)
                {
                    // Malformed entry – skip to next
                    if (_settings.Verbose)
                        Console.WriteLine("Addrv2 malformed entry: " + ex.Message);
                    break;
                }
            }

            return ret;
        }

        private static string PubkeyToOnionAddress(byte[] pubkey32)
        {
            if (pubkey32 == null || pubkey32.Length != 32)
                return null!;

            const byte version = 0x03;
            var checksumConstant = System.Text.Encoding.ASCII.GetBytes(".onion checksum");

            using var ms = new MemoryStream();
            ms.Write(checksumConstant);
            ms.Write(pubkey32);
            ms.WriteByte(version);

            byte[] checksum = new byte[2];
            var toCompute = ms.ToArray();
            var sha3 = OnixLabs.Security.Cryptography.Sha3.CreateSha3Hash256();
            var checksumArr = sha3.ComputeHash(toCompute);
            checksum[0] = checksumArr[0];
            checksum[1] = checksumArr[1];

            // Concatenate: PUBKEY (32) + CHECKSUM (2) + VERSION (1)
            byte[] toEncode = new byte[35];
            Array.Copy(pubkey32, 0, toEncode, 0, 32);
            Array.Copy(checksum, 0, toEncode, 32, 2);
            toEncode[34] = version;

            return Base32Encoding.ToString(toEncode) + ".onion";
        }

        static string HashToString(byte[] hashBytes)
        {
            if (hashBytes == null)
                throw new ArgumentNullException(nameof(hashBytes));

            if (hashBytes.Length != 32)
                //throw new ArgumentException("Bitcoin hash must be exactly 32 bytes", nameof(hashBytes));
                return "(error: must be exactly 32 bytes)";

            // Option 1: Most readable & very fast (recommended)
            var sb = new StringBuilder(64);
            for (int i = hashBytes.Length - 1; i >= 0; i--)
            {
                sb.Append(hashBytes[i].ToString("x2"));
            }
            return sb.ToString();

            // Option 2: Alternative concise version using LINQ (slightly slower, but very clean)
            // return string.Concat(hashBytes.Reverse().Select(b => b.ToString("x2")));

            // Option 3: If you already have the hash in big-endian form (e.g. from RPC or internal storage)
            // return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        void BlocksAnnouncedHouseKeeping()
        {
            try
            {
                var sortedHashes = BlocksAnnounced
                    .Select(kvp => (kvp.Key, kvp.Value.FirstSeen))
                    .OrderByDescending(e => e.FirstSeen)
                    .Select(e => e.Key)
                    .ToList();

                var toRemove = new List<String>();
                for (int i = 0; i < sortedHashes.Count; i++)
                {
                    if (i >= MAX_KEPT_BLOCKS)
                        toRemove.Add(sortedHashes[i]);
                }

                foreach (var key in toRemove)
                    BlocksAnnounced.TryRemove(key, out _);
            }
            catch (Exception ex)
            {
                MyLog($"BlocksAnnouncedHouseKeeping error: {ex.Message}");
            }
        }

        void SessionsHouseKeeping()
        {
            var toRemove = new HashSet<Guid>();
            foreach (var kvp in Sessions)
            {
                var si = kvp.Value;

                if (si.Pinned)
                    continue;

                bool stop = false;

                if (IsPeerSpammingBlocks(si))
                {
                    // Asshole...
                    MyLog($"WARNING: session '{si.UserAgent}' started on {si.Start.ToString("HH:mm:ss")} is sending too many blocks. Disconnecting...");
                    stop = true;
                    if (!String.IsNullOrEmpty(si.UserAgent))
                        SpammerUserAgentStats.AddOrUpdate(si.UserAgent, 1, (_, c) => c + 1);
                    si.SessionHistory.Spammer = true;
                }

                var sessionAge = (DateTime.Now - si.Start);

                if (_settings.DisableEvaluation)
                {
                    if (sessionAge.TotalMinutes > 1)    // Ought to be enough for anyone to handshake, right???
                    {
                        if (!String.IsNullOrEmpty(si.UserAgent))
                        {
                            if (si.GotVerack)
                                ActiveUserAgentStats.AddOrUpdate(si.UserAgent, 1, (_, c) => c + 1);
                            else
                                InactiveUserAgentStats.AddOrUpdate(si.UserAgent, 1, (_, c) => c + 1);
                        }

                        si.SessionHistory.Active = si.GotVerack;

                        // Ok, we're done with this one...
                        stop = true;
                    }
                }
                else
                {
                    // Get the "inv" message count that this peer session has and hasn't sent since it's inception...
                    GetAnnouncedBlockCount(si, out int announced, out int unannounced);

                    if (announced > 1)
                    {
                        if (!String.IsNullOrEmpty(si.UserAgent))
                            ActiveUserAgentStats.AddOrUpdate(si.UserAgent, 1, (_, c) => c + 1);

                        // Thank you for your service...
                        stop = true;
                        si.SessionHistory.Active = true;
                    }
                    else if (unannounced > 1)
                    {
                        if (!String.IsNullOrEmpty(si.UserAgent))
                            InactiveUserAgentStats.AddOrUpdate(si.UserAgent, 1, (_, c) => c + 1);

                        // Something wrong with this one. Enough of it...
                        stop = true;
                        si.SessionHistory.Active = false;
                    }
                    // else: allow it more time...
                }

                if (stop)
                {
                    //if (si.CancellationTokenSource.IsCancellationRequested)
                    //    MyLog($"WARNING: session '{si.UserAgent}' started on {si.Start.ToString("HH:mm:ss")} is already cancelled, but still lingering around...");

                    si.CancellationTokenSource.Cancel();
                    MarkAsEvaluated(si.Key, si);
                }
            }

            StateHasChanged();  // Signal UI to refresh
        }

        void AddToCollectedIfNew(String address)
        {
            if (Collected.Contains(address))
                return;

            Collected.Add(address);

            StateHasChanged();  // Signal UI to refresh
        }

        bool AddToUnvisitedIfNew(String key, PeerInfo peer)
        {
            if (Visited.Contains(key))
                return false;

            if (Unvisited.ContainsKey(key))
                return false;

            lock (Visited)
            {
                if (Visited.Contains(key))
                    return false;

                bool ret = Unvisited.TryAdd(key, peer);

                if (ret)
                    StateHasChanged();  // Signal UI to refresh

                return ret;
            }
        }

        bool MarkAsEvaluated(String key, SessionInfo sessionInfo)
        {
            bool ret = Evaluated.Add(key);
            Unvisited.TryRemove(key, out _);
            sessionInfo.SessionHistory.Evaluated = true;
            StateHasChanged();  // Signal UI to refresh
            return ret;
        }

        SessionInfo AddNewSessionInfo(String key, NetworkId networkId)
        {
            var ret = new SessionInfo(key, networkId);

            if (AllSessionHistory.ContainsKey(key))
                throw new Exception($"AllSessionHistory already contains key '{key}'");

            AllSessionHistory[key] = ret.SessionHistory;

            Sessions[ret.Id] = ret;

            return ret;
        }

        public static void MyLog(String format, params object[] args)
        {
            format = format.Replace("{", "{{");
            format = format.Replace("}", "}}");
            format = format.Replace("[", "[[");
            format = format.Replace("]", "]]");

            string text = String.Format(format, args);
            LogQueue.Add($"{DateTime.Now.ToString("dd/MMM HH:mm:ss.fff")} {text}");
            StateHasChanged();
        }

        void ConnectToNewPeers(int maxcount)
        {
            int launched = 0;

            foreach (var kvp in Unvisited)
            {
                var peer = kvp.Value;
                if (!ThrottledConnect(peer))
                    continue;

                if (++launched > maxcount)
                    break;
            }
        }

        void GetAnnouncedBlockCount(SessionInfo sessionInfo, out int announced, out int unnannounced)
        {
            announced = 0;
            unnannounced = 0;

            var startTime = sessionInfo.Start;

            var netBlocks = BlocksAnnounced.Where(p => p.Value.FirstSeen > startTime
                                    && p.Value.FirstSeen <= DateTime.Now.AddMinutes(-1)
                                    && p.Value.Count > Math.Max(p.Value.AtSessionCount / 2, 20));

            foreach (var kvp in netBlocks)
            {
                var hash = kvp.Key;
                if (sessionInfo.BlocksAnnounced.ContainsKey(hash))
                    announced++;
                else
                    unnannounced++;
            }
        }

        bool IsPeerSpammingBlocks(SessionInfo sessionInfo)
        {
            var hourly = sessionInfo.BlocksAnnounced.Where( p => (DateTime.Now - p.Value) < TimeSpan.FromHours(1) ).Count();
            if (hourly > 250)
                // Way too many...
                return true;

            var minutely = sessionInfo.BlocksAnnounced.Where(p => (DateTime.Now - p.Value) < TimeSpan.FromMinutes(1)).Count();
            if (minutely > 25)
                // Way too many...
                return true;

            return false;
        }

        bool ThrottledConnect(PeerInfo peer)
        {
            try
            {
                if (Sessions.Count > _settings.MaxActiveSessions)
                    return false;

                if (!_settings.DisableTor && (peer.NetworkId == (int)NetworkId.Tor || peer.NetworkId == (int)NetworkId.TorV3))
                {
                    if (_torConnectCount > _settings.MaxTorSimultaneousConnects)
                        return false;
                }

                string key = PeerToString(peer);

                if (Visited.Contains(key))
                    return false;

                lock (Visited)
                {
                    if (!Visited.Contains(key) && !_cancellationToken.IsCancellationRequested)
                    {
                        Visited.Add(key);
                        var sessionInfo = AddNewSessionInfo(key, (NetworkId)peer.NetworkId);
                        if (InitialPeers != null && InitialPeers.Count == 1 && !String.IsNullOrEmpty(_settings.SingleSeedHost))
                            sessionInfo.Pinned = InitialPeers!.Contains((peer.IP, peer.Port));
                        StateHasChanged();  // Signal UI to refresh

                        if ((!_settings.DisableIP && (peer.NetworkId == (int)NetworkId.IPv4 || peer.NetworkId == (int)NetworkId.IPv6))
                          || (!_settings.DisableTor && (peer.NetworkId == (int)NetworkId.Tor || peer.NetworkId == (int)NetworkId.TorV3)))
                        {
                            sessionInfo.Task = Task.Run(async () => await ConnectAndProcessPeerAsync(peer, key, sessionInfo));
                            return true;
                        }

                        // No connection will be established. Remove recently added session...
                        Sessions.Remove(sessionInfo.Id, out _);
                        StateHasChanged();  // Signal UI to refresh
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                MyLog($"ThrottledConnect error: {ex.Message}");
                Task.Delay(100).Wait();
                return false;
            }
        }

        async Task RunHttpServerAsync(string address)
        {
            var server = new MiniHttpServer(address, this);
            await server.Run();
        }

        public static void StateHasChanged()
        {
            ConsoleRenderer.RefreshEvent.Set();  // Signal UI to refresh
        }
    }
}
