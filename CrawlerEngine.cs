using BitcoinCrawlerStats.Models;
using I2PNet;
using OnixLabs.Core.Linq;
using SocksSharp.Proxy;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BitcoinCrawlerStats
{
    public class CrawlerEngine
    {
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
        internal readonly ConcurrentDictionary<string, int> ServiceStats = new ConcurrentDictionary<string, int>(); // Service flags statistics

        // Initial peers from seeds (IP:port)
        internal readonly ConcurrentBag<(IPAddress Ip, int Port)> InitialPeers = new ConcurrentBag<(IPAddress, int Port)>();

        // Visited to avoid re-crawling
        internal readonly ConcurrentHashSet<string> Collected = new ConcurrentHashSet<string>();    // Peer addresses. Collected from addr messages
        internal readonly ConcurrentDictionary<string, PeerInfo> Unvisited = new ConcurrentDictionary<string, PeerInfo>();  // Peer address -> PeerInfo
        internal readonly ConcurrentHashSet<string> Visited = new ConcurrentHashSet<string>();      // Peer addresses. Connected successfully or not
        internal readonly ConcurrentHashSet<string> Evaluated = new ConcurrentHashSet<string>();    // Peer addresses. Really tested or unable to connect

        internal readonly ConcurrentDictionary<string, BlockInfo> BlocksAnnounced = new ConcurrentDictionary<string, BlockInfo>(); // Hash -> BlockInfo

        internal ConcurrentDictionary<Guid, BitcoinSession> Sessions = new ConcurrentDictionary<Guid, BitcoinSession>();
        internal readonly ConcurrentDictionary<string, SessionHistory> AllSessionHistory = new ConcurrentDictionary<string, SessionHistory>();  // Peer address -> SessionHistory

        internal static FixedFifoQueue<String> LogQueue = new FixedFifoQueue<String>(MAX_VISIBLE_LOG_ENTRIES);

        internal LiveStatistics LiveStatistics => _liveStatistics;
        internal ConsoleRenderer? Renderer => _renderer;
        public CrawlerCommandLineSettings Settings => _settings;

        // Max peers to crawl (limit to avoid overwhelming the network or your machine)
        //private const int MaxPeersToCrawl = 5000;

        private ProxyClient<Socks5>? _proxyClient;
        private I2PSession? _samSession;

        LiveStatistics _liveStatistics = new LiveStatistics();

        int _torConnectCount = 0;
        //object _connectLock = new object();

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

                    AddToCollectedIfNew(BitcoinSession.PeerToString(item.Value));
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

            if (!_settings.DisableTor)
            {
                _proxyClient = new ProxyClient<Socks5>();
                _proxyClient.Settings = proxySettings;
            }

            if (!Settings.DisableI2P)
            {
                try
                {
                    // Connect to SAM bridge
                    _samSession = new I2PSession(samPort: _settings.SamPort, samIPaddress: Dns.GetHostAddresses(_settings.SamHost!).FirstOrDefault());
                    await _samSession.InitializeAsync();
                }
                catch (Exception ex)
                {
                    throw new Exception($"Unable to connect to SAM (I2P) bridge at {_settings.SamHost}:{_settings.SamPort} : {ex.Message}");
                }
            }

            _stopwatch = Stopwatch.StartNew();

            _renderer = new ConsoleRenderer(this, _stopwatch, _settings, _cancellationToken);
            if (!_settings.DisableConsoleRefresh)
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
                si.Close();

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

            _renderer?.PrintStatistics();

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

        internal void BlocksAnnouncedHouseKeeping()
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

                if (IsPeerSpammingBlocks(si.SessionInfo))
                {
                    // Asshole...
                    MyLog($"WARNING: session '{si.UserAgent}' started on {si.Start.ToString("HH:mm:ss")} is sending too many blocks. Disconnecting...");
                    stop = true;
                    if (!String.IsNullOrEmpty(si.UserAgent))
                        SpammerUserAgentStats.AddOrUpdate(si.UserAgent, 1, (_, c) => c + 1);
                    si.MarkAsSpammer();
                }

                var sessionAge = (DateTime.Now - si.Start);

                if (_settings.DisableEvaluation)
                {
                    if (sessionAge.TotalSeconds > 30)    // Ought to be enough for anyone to handshake, right???
                    {
                        var active = si.GotVerack;
                        if (!String.IsNullOrEmpty(si.UserAgent))
                        {
                            if (active)
                                ActiveUserAgentStats.AddOrUpdate(si.UserAgent, 1, (_, c) => c + 1);
                            else
                                InactiveUserAgentStats.AddOrUpdate(si.UserAgent, 1, (_, c) => c + 1);
                        }

                        si.MarkAsActive(active);

                        // Ok, we're done with this one...
                        stop = true;
                    }
                }
                else
                {
                    // Get the "inv" message count that this peer session has and hasn't sent since it's inception...
                    GetAnnouncedBlockCount(si.SessionInfo, out int announced, out int unannounced);

                    if (announced > 1)
                    {
                        if (!String.IsNullOrEmpty(si.UserAgent))
                            ActiveUserAgentStats.AddOrUpdate(si.UserAgent, 1, (_, c) => c + 1);

                        // Thank you for your service...
                        stop = true;
                        si.MarkAsActive(true);
                    }
                    else if (unannounced > 1)
                    {
                        if (!String.IsNullOrEmpty(si.UserAgent))
                            InactiveUserAgentStats.AddOrUpdate(si.UserAgent, 1, (_, c) => c + 1);

                        // Something wrong with this one. Enough of it...
                        stop = true;
                        si.MarkAsActive(false);
                    }
                    // else: allow it more time...
                }

                if (stop)
                {
                    //if (si.CancellationTokenSource.IsCancellationRequested)
                    //    MyLog($"WARNING: session '{si.UserAgent}' started on {si.Start.ToString("HH:mm:ss")} is already cancelled, but still lingering around...");

                    MarkAsEvaluated(si.SessionInfo);
                    si.Close();
                }
            }

            StateHasChanged();  // Signal UI to refresh
        }

        internal void AddToCollectedIfNew(String address)
        {
            if (Collected.Contains(address))
                return;

            Collected.Add(address);

            StateHasChanged();  // Signal UI to refresh
        }

        internal bool AddToUnvisitedIfNew(String key, PeerInfo peer)
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

        private async Task ConnectAndProcessPeerAsync(PeerInfo peer, BitcoinSession bitcoinSession)
        {
            TcpClient? client = null;
            Stream? stream = null;

            int networkId = peer.NetworkId;
            bool isOnion = (networkId == (int)NetworkId.Tor || networkId == (int)NetworkId.TorV3);
            var sessionInfo = bitcoinSession.SessionInfo;
            try
            {
                var cts = sessionInfo.CancellationTokenSource;
                try
                {
                    if (isOnion && _proxyClient != null)
                    {
                        try
                        {
                            Interlocked.Increment(ref _torConnectCount);
                            stream = _proxyClient.GetDestinationStream(peer.Host, peer.Port); // .onion resolves via Tor
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
                    else if (networkId == (int)NetworkId.i2p && _samSession != null)
                    {
                        stream = await _samSession.ConnectAsync(peer.Host);

                        Interlocked.Increment(ref _liveStatistics.I2pSuccess);
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
                        if (networkId == (int)NetworkId.i2p)
                            Interlocked.Increment(ref _liveStatistics.I2pErrors);

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

                await bitcoinSession.ProcessPeerStreamAsync(stream, sessionInfo, peer);
            }

            catch (Exception ex)
            {
                if (sessionInfo.SessionHistory.Evaluated is not true)    // Otherwise, it means session was closed by us.
                {
                    if (ex is OperationCanceledException || ex is TaskCanceledException)
                    {
                        // Program is shutting down
                        // Session hasn't been fully evaluated, so don't persist...
                        sessionInfo.SessionHistory.Ignore = true;
                        // We don't want to do anything else, not even add peer to the Evaluated list...
                        return;
                    }

                    if (stream != null)
                    {
                        Interlocked.Increment(ref _liveStatistics.StreamErrors);
                        sessionInfo.SessionHistory.StreamError = ex.Message;
                    }
                    MarkAsEvaluated(sessionInfo);   // Keep it in the evaluated set, so we don't connect to it again...
                }
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

        bool MarkAsEvaluated(SessionInfo sessionInfo)
        {
            var key = sessionInfo.Key;
            bool ret = Evaluated.Add(key);
            Unvisited.TryRemove(key, out _);
            sessionInfo.SessionHistory.Evaluated = true;
            StateHasChanged();  // Signal UI to refresh
            return ret;
        }

        BitcoinSession AddNewSession(String key, NetworkId networkId)
        {
            var ret = new BitcoinSession(key, networkId, this);
            var si = ret.SessionInfo;

            //if (AllSessionHistory.ContainsKey(key))
            //    throw new Exception($"AllSessionHistory already contains key '{key}'");

            AllSessionHistory[key] = si.SessionHistory;

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

            // First, a bit of cleanup (unsupported/ignored protocols, etc)...
            var toRemove = new List<String>();
            foreach (var kvp in Unvisited)
            {
                if (this.Visited.Contains(kvp.Key))
                    toRemove.Add(kvp.Key);
            }
            foreach (var key in toRemove)
                this.Unvisited.TryRemove(key, out _);
            
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

                string key = BitcoinSession.PeerToString(peer);

                if (Visited.Contains(key))
                    return false;

                lock (Visited)
                {
                    if (!Visited.Contains(key) && !_cancellationToken.IsCancellationRequested)
                    {
                        Visited.Add(key);
                        var bitcoinSession = AddNewSession(key, (NetworkId)peer.NetworkId);
                        var sessionInfo = bitcoinSession.SessionInfo;
                        if (InitialPeers != null && InitialPeers.Count == 1 && !String.IsNullOrEmpty(_settings.SingleSeedHost))
                            sessionInfo.Pinned = InitialPeers!.Contains((peer.IP, peer.Port));
                        StateHasChanged();  // Signal UI to refresh

                        if ((!_settings.DisableIP && (peer.NetworkId == (int)NetworkId.IPv4 || peer.NetworkId == (int)NetworkId.IPv6))
                          || (!_settings.DisableTor && (peer.NetworkId == (int)NetworkId.Tor || peer.NetworkId == (int)NetworkId.TorV3))
                          || (!_settings.DisableI2P && (peer.NetworkId == (int)NetworkId.i2p)))
                        {
                            sessionInfo.Task = Task.Run(async () => await ConnectAndProcessPeerAsync(peer, bitcoinSession));
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
