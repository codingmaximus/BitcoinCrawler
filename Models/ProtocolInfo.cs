using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats.Models
{
    public class ProtocolInfo : GenericStatisticRecord
    {
        public ProtocolInfo(string id, int count) : base(id, count)
        {
        }
    }
}
