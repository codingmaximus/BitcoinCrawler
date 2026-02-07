using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats.Models
{
    public interface IGenericStatisticRecord
    {
        public String Id { get; }
        public int Count { get; set; }
    }
}
