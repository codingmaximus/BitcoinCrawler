using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats.Models
{
    public class ProtocolInfo : IGenericStatisticRecord
    {
        public required String Id { get; set; }
        public int Count { get; set; }
    }
}
