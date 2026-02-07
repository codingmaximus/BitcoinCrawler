using BitcoinCrawlerStats.Models;

namespace BitcoinCrawlerStats
{
    class SessionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
        public String? UserAgent;
        public String Key { get; }

        public DateTime Start { get; } = DateTime.Now;

        public bool GotVerack { get; private set; }
        public void HandshakeComplete()
        {
            this.GotVerack = true;
            this.SessionHistory.GotVerack = true;
        }

        public DateTime LastReceive;
        public String? LastMessage;

        public int Addresses;
        public int AddrMessagesRcvd;

        public bool Pinned;

        public MemoryStream? MessageBuffer;
        public uint WantedLength;

        public Dictionary<String, DateTime> BlocksAnnounced { get; } = new Dictionary<String, DateTime>();

        public NetworkId NetworkId { get; }

        public CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();

        public SessionHistory SessionHistory { get; }

        public Task? Task;

        public SessionInfo(string key, NetworkId networkId)
        {
            this.Key = key;
            this.NetworkId = networkId;
            this.SessionHistory = new SessionHistory(key, (int)networkId);
        }
    }
}
