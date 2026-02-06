using BitcoinCrawlerStats.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats
{
    public class CrawlerPersistence
    {
        readonly CrawlerEngine _engine;
        readonly AppDbContext _db;

        public AppDbContext? DbContext => _db;

        public CrawlerPersistence(CrawlerEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={_engine.Settings.DbFilePath}")
                .Options;

            _db = new AppDbContext(options);
        }

        public async Task<bool> StartAsync()
        {
            await _db.Database.EnsureCreatedAsync();   // creates both tables + FK

            if (!await _db.Unvisited.AnyAsync() && await _db.Evaluated.AnyAsync())
            {
                Console.WriteLine("Database contains Evaluated peers but no Unvisited peers. Is crawling done?");
                Console.WriteLine("If not, reset database and try again.");
                return false;
            }

            return true;
        }

        public async Task LoadFromDbAsync()
        {
            await LoadUserAgentsTable<UserAgentInfo>(_engine.UserAgentStats);
            await LoadUserAgentsTable<ActiveUserAgentInfo>(_engine.ActiveUserAgentStats);
            await LoadUserAgentsTable<InactiveUserAgentInfo>(_engine.InactiveUserAgentStats);
            await LoadUserAgentsTable<SpammerUserAgentInfo>(_engine.SpammerUserAgentStats);

            await LoadProtocolsTable(_db.ProtocolStats, _engine.ProtocolStats);

            await LoadHostsTable(_db.Evaluated, _engine.Evaluated);
            await LoadPeersTable(_db.Unvisited, _engine.Unvisited);
        }

        public Task SaveToDbAsync()
        {
            return PersistDataAsync();
        }

        public async Task ResetDatabaseAsync()
        {
            await _db.UserAgents.ExecuteDeleteAsync();
            await _db.ActiveUserAgents.ExecuteDeleteAsync();
            await _db.InactiveUserAgents.ExecuteDeleteAsync();
            await _db.SpammerUserAgents.ExecuteDeleteAsync();
        }

        async Task PersistDataAsync()
        {
            await RunAndHandleError("UserAgentStats", 
                                async () => await PersistUserAgentsTable<UserAgentInfo>(_engine.UserAgentStats));
            await RunAndHandleError("ActiveUserAgentStats", 
                                async () => await PersistUserAgentsTable<ActiveUserAgentInfo>(_engine.ActiveUserAgentStats));
            await RunAndHandleError("InactiveActiveUserAgentStats", 
                                async () => await PersistUserAgentsTable<InactiveUserAgentInfo>(_engine.InactiveUserAgentStats));
            await RunAndHandleError("SpammerUserAgentStats", 
                                async () => await PersistUserAgentsTable<SpammerUserAgentInfo>(_engine.SpammerUserAgentStats));
            await RunAndHandleError("ProtocolStats", 
                                async () => await PersistProtocolsTable(_db.ProtocolStats, _engine.ProtocolStats));
            await RunAndHandleError("Unvisited", 
                                async () => await PersistPeersTable(_db.Unvisited, _engine.Unvisited));

#if DEBUG
            //var myUnvisited = new Dictionary<String, PeerInfo>();
            //await LoadPeersTable(_db.Unvisited, myUnvisited);
#endif //DEBUG

            await RunAndHandleError("Evaluated", 
                                async () => await PersistHostsTable(_db.Evaluated, _engine.Evaluated));
            await RunAndHandleError("SessionHistory", 
                                async () => await PersistSessionHistory(_engine.AllSessionHistory));
        }

        private async Task RunAndHandleError(string stage, Func<Task> asyncAction)
        {
            try
            {
                await asyncAction();
            }
            catch (Exception ex)
            {
                CrawlerEngine.MyLog($"Error persisting {stage} data: {ex.Message}");
            }
        }

        async Task<int> PersistUserAgentsTable<T>(IReadOnlyDictionary<String, int> dict) where T : class, IGenericStatisticRecord
        {
            DbSet<T> dbSet = _db.Set<T>();

            var dbUserAgents = await dbSet.ToDictionaryAsync(p => p.Id, p => p);

            var dictCopy = new Dictionary<String, int>(dict); // Use a copy to avoid concurrent changes...

            // First update existing...
            var userAgents = dictCopy
                                    .Where(p => dbUserAgents.ContainsKey(p.Key) && dbUserAgents[p.Key].Count != p.Value)
                                    .Select(p =>
                                    {
                                        var entity = dbUserAgents[p.Key];
                                        entity.Count = p.Value;
                                        return entity;
                                    }).ToList();
            if (userAgents.Count > 0)
                dbSet.UpdateRange(userAgents);

            // Then add new...
            var newUserAgents = dictCopy
                                    .Where(p => !dbUserAgents.ContainsKey(p.Key))
                                    .Select(p =>
                                    {
                                        var instance = (T)Activator.CreateInstance(typeof(T), p.Key, p.Value)!;
                                        return instance;
                                    })
                                    .ToList();
            if (newUserAgents.Count > 0)
                dbSet.AddRange(newUserAgents);

            try
            {
                return await _db.SaveChangesAsync();
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }

        async Task<int> LoadUserAgentsTable<T>(IDictionary<String, int> dict) where T : class, IGenericStatisticRecord
        {
            dict.Clear();
            var dbSet = _db.Set<T>();
            var dbDict = await dbSet.ToDictionaryAsync(p => p.Id, p => p.Count);
            foreach (var kvp in dbDict)
                if (!dict.ContainsKey(kvp.Key))
                    dict[kvp.Key] = kvp.Value;

            return dict.Count;
        }

        async Task PersistProtocolsTable(DbSet<ProtocolInfo> dbSet, IReadOnlyDictionary<String, int> dict)
        {
            var dbEntries = await dbSet.ToDictionaryAsync(p => p.Id, p => p);

            var dictCopy = new Dictionary<String, int>(dict); // Use a copy to avoid concurrent changes...

            // First update existing...
            var userAgents = dictCopy
                                    .Where(p => dbEntries.ContainsKey(p.Key) && dbEntries[p.Key].Count != p.Value)
                                    .Select(p =>
                                    {
                                        var entity = dbEntries[p.Key];
                                        entity.Count = p.Value;
                                        return entity;
                                    }).ToList();
            if (userAgents.Count > 0)
                dbSet.UpdateRange(userAgents);

            // Then add new...
            var newEntries = dictCopy
                                    .Where(p => !dbEntries.ContainsKey(p.Key))
                                    .Select(p => new ProtocolInfo { Id = p.Key, Count = p.Value })
                                    .ToList();
            if (newEntries.Count > 0)
                dbSet.AddRange(newEntries);

            try
            {
                await _db.SaveChangesAsync();
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }

        async Task<int> LoadProtocolsTable(DbSet<ProtocolInfo> dbSet, IDictionary<String, int> dict)
        {
            dict.Clear();
            var dbDict = await dbSet.ToDictionaryAsync(p => p.Id, p => p.Count);
            foreach (var kvp in dbDict)
                if (!dict.ContainsKey(kvp.Key))
                    dict[kvp.Key] = kvp.Value;

            return dict.Count;
        }

        async Task PersistHostsTable(DbSet<HostInfo> dbSet, IEnumerable<String> list)
        {
            var dbEntries = new HashSet<String>(await dbSet.Where(p => !String.IsNullOrEmpty(p.Id))
                                                           .Select(p => p.Id)
                                                           .ToListAsync());

            // Add new...
            var newEntries = list
                                    .ToList()
                                    .Where(p => !dbEntries.Contains(p))
                                    .Select(p => new HostInfo { Id = p })
                                    .ToList();

            if (newEntries.Count == 0)
                return;

            dbSet.AddRange(newEntries);

            try
            {
                await _db.SaveChangesAsync();
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }

        async Task<int> LoadHostsTable(DbSet<HostInfo> dbSet, ConcurrentHashSet<String> list)
        {
            list.Clear();
            var dbList = await dbSet.ToListAsync();
            foreach (var item in dbList)
                list.Add(item.Id);

            return list.Count;
        }

        async Task PersistPeersTable(DbSet<PeerInfo> dbSet, IReadOnlyDictionary<String, PeerInfo> dict)
        {
            var dbEntries = new Dictionary<String, PeerInfo>(await dbSet.Where(p => !String.IsNullOrEmpty(p.Key))
                                                           .ToDictionaryAsync(p => p.Key!, p => p));

#if DEBUG
            //var ipv4Peer = dbEntries.Where(p => p.Value.NetworkId == (int)NetworkId.IPv4).FirstOrDefault().Value;
            //var ipv6Peer = dbEntries.Where(p => p.Value.NetworkId == (int)NetworkId.IPv6).FirstOrDefault().Value;
            //var tor3Peer = dbEntries.Where(p => p.Value.NetworkId == (int)NetworkId.TorV3).FirstOrDefault().Value;
#endif //DEBUG

            var peerDict = new Dictionary<String, PeerInfo>(dict);
            foreach (var item in peerDict.Values)
                if (String.IsNullOrEmpty(item.Host))
                    item.Host = item.IP?.ToString();

            // Add new...
            var newEntries = peerDict
                                    .Where(p => !dbEntries.ContainsKey(p.Key))
                                    .Select(p => new PeerInfo
                                    {
                                        Key = p.Key,
                                        NetworkId = p.Value.NetworkId,
                                        Host = p.Value.Host,
                                        Port = p.Value.Port
                                    })
                                    .ToList();

            if (newEntries.Count == 0)
                return;

            int invalid = 0;
            foreach(var peer in newEntries)
                if (peer.NetworkId == (int)NetworkId.IPv6
                    && peer.IP != null
                    && !IPAddress.TryParse(peer.IP.ToString(), out _))
                    invalid++;

            if (invalid > 0)
                CrawlerEngine.MyLog($"WARNING: found {invalid} invalid IPv6 addresses during PersistPeersTable");

            dbSet.AddRange(newEntries);

            // Remove what is no longer in memory...
            var toRemove = dbEntries.Where(p => !peerDict.ContainsKey(p.Key))
                                    .Select(p => p.Value)
                                    .ToList();
            if (toRemove.Any())
                dbSet.RemoveRange(toRemove);

            try
            {
                await _db.SaveChangesAsync();
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }

        async Task<int> LoadPeersTable(DbSet<PeerInfo> dbSet, IDictionary<String, PeerInfo> dict)
        {
            dict.Clear();
            var dbEntries = await dbSet.Where(p => !String.IsNullOrEmpty(p.Key)).ToListAsync();

#if DEBUG
            //var ipv4Peer = dbEntries.Where(p => p.NetworkId == (int)NetworkId.IPv4).FirstOrDefault();
            //var ipv6Peer = dbEntries.Where(p => p.NetworkId == (int)NetworkId.IPv6).FirstOrDefault();
            //var tor3Peer = dbEntries.Where(p => p.NetworkId == (int)NetworkId.TorV3).FirstOrDefault();
#endif //DEBUG

            foreach (var item in dbEntries)
            {
                try
                {
                    if (String.IsNullOrEmpty(item.Key))
                        continue;

                    IPAddress? ip = null;
                    if (item.NetworkId == (int)NetworkId.IPv4 || item.NetworkId == (int)NetworkId.IPv6)
                        IPAddress.TryParse(item.Host, out ip);

                    dict[item.Key] = new PeerInfo
                    {
                        NetworkId = item.NetworkId,
                        Host = item.Host!,
                        IP = ip,
                        Port = item.Port
                    };
                }
                catch
                {
                    CrawlerEngine.MyLog($"LoadPeersTable: error loading PeerInfo: Key = '{item.Key}'");
                }
            }

            return dict.Count;
        }

        async Task PersistSessionHistory(ConcurrentDictionary<String, SessionHistory> dict)
        {
            var dbSet = _db.SessionHistory;
            var dbEntries = await dbSet.ToDictionaryAsync(p => p.Key, p => p);

            var dictCopy = new Dictionary<String, SessionHistory>(dict); // Use a copy to avoid concurrent changes...

            // First update existing...
            var sessionHistories = dictCopy.Where(p => dbEntries.ContainsKey(p.Key) && !p.Value.Ignore)
                                    .Select(p =>
                                    {
                                        var sh = p.Value;

                                        var entity = dbEntries[p.Key];

                                        entity.UserAgent = sh.UserAgent;
                                        entity.Connected = sh.Connected;
                                        entity.ConnectionError = sh.ConnectionError;
                                        entity.GotVerack = sh.GotVerack;
                                        entity.StreamError = sh.StreamError;
                                        entity.Evaluated = sh.Evaluated;
                                        entity.Active = sh.Active;
                                        entity.Spammer = sh.Spammer;
                                        entity.LoopFinished = sh.LoopFinished;

                                        return entity;
                                    }).ToList();
            if (sessionHistories.Count > 0)
                dbSet.UpdateRange(sessionHistories);

            // Then add new...
            var newEntries = dictCopy.Where(p => !dbEntries.ContainsKey(p.Key) && !p.Value.Ignore)
                                    .Select(p => p.Value)
                                    .ToList();
            if (newEntries.Count > 0)
                dbSet.AddRange(newEntries);

            var ignoredEntries = dictCopy.Where(p => dbEntries.ContainsKey(p.Key) && p.Value.Ignore)
                                    .Select(p => dbEntries[p.Key])
                                    .ToList();
            if (ignoredEntries.Any())
                dbSet.RemoveRange(ignoredEntries);

            try
            {
                await _db.SaveChangesAsync();
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }

        bool IsValidIPAddress(String key, int networkId)
        {
            if (networkId != (int)NetworkId.IPv6)
                // Assume valid...
                return true;

            return IPAddress.TryParse(key, out _);
        }

        String SafeExtractHost(string key)
        {
            if (String.IsNullOrEmpty(key))
                return key;

            var pos = key.LastIndexOf(':');
            if (pos == -1)
                return key;

            var squarePos = key.IndexOf("]");
            if (squarePos == -1)
                return key.Substring(0, pos);

            if (squarePos < pos)
                return key.Substring(0, pos);

            return key.Substring(0, pos) + key.Substring(squarePos);
        }
    }
}
