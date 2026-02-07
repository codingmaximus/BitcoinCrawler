using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats.Models
{
    public class PeerInfo
    {
        public String? Key { get; set; }
        public String? Host { get; set; }
        public int NetworkId { get; set; }
        public int Port { get; set; }
        internal IPAddress? IP;

        public PeerInfo()
        { }

        internal PeerInfo((int networkId, String host, IPAddress ip, int port) peer)
        {
            this.NetworkId = peer.networkId;
            this.Host = peer.host;
            this.IP = peer.ip;
            this.Port = peer.port;
        }
    }
}
