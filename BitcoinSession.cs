using BitcoinCrawlerStats.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using System.Text;

using static BitcoinCrawlerStats.StringUtils;

namespace BitcoinCrawlerStats
{
    class BitcoinSession
    {
        // Bitcoin mainnet magic bytes
        private static readonly uint Magic = 0xD9B4BEF9;

        // Protocol version we advertise (current as of late 2025 ~70016 or higher; 70015 is safe)
        private const int ProtocolVersion = 70016;

        // Services we advertise (NODE_NETWORK)
        private static readonly ulong Services = 1;

        readonly CrawlerEngine _engine;
        readonly CrawlerCommandLineSettings _settings;

        public SessionInfo SessionInfo { get; }

        public Guid Id => this.SessionInfo.Id;
        public String? UserAgent => this.SessionInfo.UserAgent;
        public bool GotVerack => this.SessionInfo.GotVerack;
        public DateTime Start => this.SessionInfo.Start;
        public Task? Task => this.SessionInfo.Task;
        public bool Pinned => this.SessionInfo.Pinned;

        // Service flags

        [Flags]
        public enum ServiceFlags
        {
            // NODE_NETWORK means that the node is capable of serving the complete block chain. It is currently
            // set by all Bitcoin Core non pruned nodes, and is unset by SPV clients or other light clients.
            NODE_NETWORK = (1 << 0),
            // NODE_BLOOM means the node is capable and willing to handle bloom-filtered connections.
            NODE_BLOOM = (1 << 2),
            // NODE_WITNESS indicates that a node can be asked for blocks and transactions including
            // witness data.
            NODE_WITNESS = (1 << 3),
            // NODE_COMPACT_FILTERS means the node will service basic block filter requests.
            // See BIP157 and BIP158 for details on how this is implemented.
            NODE_COMPACT_FILTERS = (1 << 6),
            // NODE_NETWORK_LIMITED means the same as NODE_NETWORK with the limitation of only
            // serving the last 288 (2 day) blocks
            // See BIP159 for details on how this is implemented.
            NODE_NETWORK_LIMITED = (1 << 10),
            // NODE_UASF_REDUCED_DATA means the node enforces UASFReducedData rules as applicable
            NODE_UASF_REDUCED_DATA = (1 << 27),
        }

        public BitcoinSession(String key, NetworkId networkId, CrawlerEngine engine)
        {
            this.SessionInfo = new SessionInfo(key, networkId);
            _engine = engine;
            _settings = _engine.Settings;
        }

        public void MarkAsActive(bool active)
        {
            this.SessionInfo.SessionHistory.Active = active;
        }

        public void MarkAsSpammer()
        {
            this.SessionInfo.SessionHistory.Spammer = true;
        }

        public void Close()
        {
            this.SessionInfo.CancellationTokenSource.Cancel();
        }

        internal async Task ProcessPeerStreamAsync(Stream stream, SessionInfo sessionInfo, PeerInfo peerInfo, bool pinned = false)
        {
            var contextStr = peerInfo.Host ?? peerInfo.IP?.ToString()!;
            MemoryStream? msInner = null;
            try
            {
                _engine.ProtocolStats.AddOrUpdate(sessionInfo.NetworkId.ToString(), 1, (_, c) => c + 1);

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
                            var userAgent = ParseUserAgent(payload, out ulong services);
                            sessionInfo.SessionHistory.Services = services;
                            if (!string.IsNullOrEmpty(userAgent))
                            {
                                sessionInfo.UserAgent = userAgent;
                                sessionInfo.SessionHistory.UserAgent = userAgent;
                                _engine.UserAgentStats.AddOrUpdate(userAgent, 1, (_, c) => c + 1);
                            }

                            if ((services & (int)ServiceFlags.NODE_NETWORK) != 0)
                                _engine.ServiceStats.AddOrUpdate(nameof(ServiceFlags.NODE_NETWORK), 1, (_, c) => c + 1);

                            if ((services & (int)ServiceFlags.NODE_NETWORK_LIMITED) != 0)
                                _engine.ServiceStats.AddOrUpdate(nameof(ServiceFlags.NODE_NETWORK_LIMITED), 1, (_, c) => c + 1);

                            if ((services & (int)ServiceFlags.NODE_UASF_REDUCED_DATA) != 0)
                            {
                                sessionInfo.SessionHistory.HasBip110 = true;
                                _engine.ServiceStats.AddOrUpdate(nameof(ServiceFlags.NODE_UASF_REDUCED_DATA), 1, (_, c) => c + 1);
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
                                    _engine.AddToCollectedIfNew(key);
                                    _engine.AddToUnvisitedIfNew(key, newPeerInfo);
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

                                if (_settings.EnableTor)
                                {
                                    var torAddresses = addresses.Where(p => p.networkId == (int)NetworkId.TorV3).ToList();  // Tor v3
                                    foreach (var torAddr in torAddresses)
                                    {
                                        Interlocked.Increment(ref sessionInfo.Addresses);

                                        var newPeerInfo = new PeerInfo(((int)NetworkId.TorV3, torAddr.host, null!, torAddr.port));
                                        var key = PeerToString(newPeerInfo);
                                        _engine.AddToCollectedIfNew(key);
                                        _engine.AddToUnvisitedIfNew(key, newPeerInfo);
                                    }
                                }

                                List<(int, string, IPAddress, int)> peerAddresses = new List<(int, string, IPAddress, int)>();

                                if (!_settings.DisableIP)
                                    peerAddresses.AddRange(
                                                addresses
                                                    .Where(p => p.networkId == (int)NetworkId.IPv4 || p.networkId == (int)NetworkId.IPv6)
                                                    .ToList()
                                            );

                                if (_settings.EnableI2P)
                                    peerAddresses.AddRange(
                                                addresses
                                                    .Where(p => p.networkId == (int)NetworkId.i2p)
                                                    .ToList()
                                            );

                                foreach (var peer in peerAddresses)
                                {
                                    Interlocked.Increment(ref sessionInfo.Addresses);

                                    var newPeerInfo = new PeerInfo(peer);
                                    var key = PeerToString(newPeerInfo);
                                    _engine.AddToCollectedIfNew(key);
                                    _engine.AddToUnvisitedIfNew(key, newPeerInfo);
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
                    CrawlerEngine.MyLog($"{context}: PRS: near the end of buffer ({stream.Length - stream.Position} bytes), returning false");
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
                    CrawlerEngine.MyLog($"{context}: PRS: still not enough ({stream.Length} - {stream.Position} < {length}), returning false");
                return false;
            }

            var pay = reader.ReadBytes((int)length);
            //if (BitConverter.ToUInt32(ComputeChecksum(pay), 0) != checksum) return false;
            if (ComputeChecksum(pay) != checksum)
            {
                if (_settings.DebugParse)
                    CrawlerEngine.MyLog($"{context}: PRS: Wrong checksum, returning false");
                return false;
            }

            command = cmd;
            payload = pay;
            //MyLog($"{context}: PRS: '{cmd}' message parsed!");
            sessionInfo.WantedLength = 0;
            return true;
        }

        #region P2P Protocol stuff (some AI-generated code here...) 

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
                            _engine.BlocksAnnounced.AddOrUpdate(
                                    hashStr,
                                    new BlockInfo { Count = 1, FirstSeen = DateTime.Now, LastReceived = DateTime.Now, AtSessionCount = _engine.Sessions.Count },  // Keep track of the current session count
                                    (_, c) => new BlockInfo { Count = c.Count + 1, LastReceived = DateTime.Now, AtSessionCount = c.AtSessionCount, FirstSeen = c.FirstSeen }
                                );

                            sessionInfo.BlocksAnnounced[hashStr] = DateTime.Now;

                            _engine.BlocksAnnouncedHouseKeeping();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ParseInvMessage: ERROR: " + ex.Message);
            }
        }

        static string ParseUserAgent(byte[] payload, out ulong services)
        {
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms);

            reader.ReadInt32();                 // version
            services = reader.ReadUInt64();     // services
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

        private static byte[] BuildNoncePayload()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write((ulong)new Random().NextInt64()); // nonce

            return ms.ToArray();
        }

        private static uint ComputeChecksum(byte[] payload)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Security.Cryptography.SHA256.HashData(payload));
            return BitConverter.ToUInt32(hash, 0);
        }

        static string PeerToString(IPAddress ip, int port)
        {
            return (ip != null && ip.AddressFamily == AddressFamily.InterNetworkV6)
                ? $"[{ip}]:{port}"
                : $"{ip}:{port}";
        }

        public static string PeerToString(PeerInfo peer)
        {
            if (peer.NetworkId == (int)NetworkId.IPv4 || peer.NetworkId == (int)NetworkId.IPv6)
                return PeerToString(peer.IP!, peer.Port);

            return $"{peer.Host}:{peer.Port}";
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
                    if (networkId == (byte)NetworkId.IPv4)
                    {
                        // IPV4

                        var ipBytes = reader.ReadBytes(4);
                        port = IPAddress.NetworkToHostOrder(reader.ReadInt16());

                        ip = new IPAddress(ipBytes);
                        host = ip.ToString();
                    }
                    else if (networkId == (byte)NetworkId.IPv6)
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
                    else if (networkId == (byte)NetworkId.TorV3)
                    {
                        // TOR V3

                        var pubkeyBytes = reader.ReadBytes(32);
                        //var port = stream.BigEndianReadUInt16();
                        port = (short)((reader.ReadByte() << 8) | reader.ReadByte());

                        // Convert to .onion string using Tor's standard encoding
                        host = PubkeyToOnionAddress(pubkeyBytes);
                    }
                    else if (networkId == (byte)NetworkId.i2p)
                    {
                        // I2P

                        var i2pAddressBytes = reader.ReadBytes(32);

                        port = (short)((reader.ReadByte() << 8) | reader.ReadByte());
                        host = Base32Encoding.ToString(i2pAddressBytes) + ".b32.i2p";

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

        private static byte[] BuildHeadersPayload(int count)
        {
            return new byte[] { 0x00 };
        }

        #endregion  Protocol stuff
    }
}
