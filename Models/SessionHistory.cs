using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats.Models
{
    public class SessionHistory
    {
        /// <summary>
        /// Peer address
        /// </summary>
        public string Key { get; set; }
        public string? UserAgent { get; set; }
        public int NetworkId { get; set; }

        public bool? Connected { get; set; }
        public String? ConnectionError { get; set; }

        public bool? GotVerack { get; set; }
        public String? StreamError { get; set; }

        public ulong Services { get; set; }
        public bool FullNode { get; set; }
        public bool HasBip110 { get; set; }

        public bool? Evaluated { get; set; }
        public bool? Active { get; set; }
        public bool Spammer { get; set; }
        public bool LoopFinished { get; set; }

        public bool Ignore;

        public SessionHistory(string key, int networkId)
        {
            this.Key = key;
            this.NetworkId = networkId;
        }
    }
}
