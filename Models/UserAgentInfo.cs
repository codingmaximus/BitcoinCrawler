using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats.Models
{
    public class GenericStatisticRecord : IGenericStatisticRecord
    {
        public required String Id { get; set; }
        public int Count { get; set; }

        public GenericStatisticRecord(String id, int count)
        {
            Id = id;
            Count = count;
        }
    }

    public class UserAgentInfo : GenericStatisticRecord
    {
        public UserAgentInfo(string id, int count) : base(id, count)
        {
        }
    }

    public class ActiveUserAgentInfo : GenericStatisticRecord
    {
        public ActiveUserAgentInfo(string id, int count) : base(id, count)
        {
        }
    }

    public class InactiveUserAgentInfo : GenericStatisticRecord
    {
        public InactiveUserAgentInfo(string id, int count) : base(id, count)
        {
        }
    }

    public class SpammerUserAgentInfo : GenericStatisticRecord
    {
        public SpammerUserAgentInfo(string id, int count) : base(id, count)
        {
        }
    }
}
