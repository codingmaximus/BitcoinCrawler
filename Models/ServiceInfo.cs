using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats.Models
{
    public class ServiceInfo : GenericStatisticRecord
    {
        public ServiceInfo(string id, int count) : base(id, count)
        {
        }
    }
}
