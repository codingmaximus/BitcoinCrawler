using BitcoinCrawlerStats.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats
{
    public class AppDbContext : DbContext
    {
        public DbSet<UserAgentInfo> UserAgents { get; set; } = null!;
        public DbSet<ActiveUserAgentInfo> ActiveUserAgents { get; set; } = null!;
        public DbSet<InactiveUserAgentInfo> InactiveUserAgents { get; set; } = null!;
        public DbSet<SpammerUserAgentInfo> SpammerUserAgents { get; set; } = null!;

        public DbSet<ProtocolInfo> ProtocolStats { get; set; } = null!;

        public DbSet<HostInfo> Evaluated { get; set; } = null!;
        public DbSet<PeerInfo> Unvisited { get; set; } = null!;

        public DbSet<SessionHistory> SessionHistory { get; set; } = null!;

        // Don't keep blocks announced for now
        // private readonly ConcurrentDictionary<string, BlockInfo> BlocksAnnounced = new ConcurrentDictionary<string, BlockInfo>();

        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserAgentInfo>()
                .ToTable("UserAgents");
            modelBuilder.Entity<ActiveUserAgentInfo>()
                .ToTable("ActiveUserAgents");
            modelBuilder.Entity<InactiveUserAgentInfo>()
                .ToTable("InactiveUserAgents");
            modelBuilder.Entity<SpammerUserAgentInfo>()
                .ToTable("SpammerUserAgents");

            modelBuilder.Entity<PeerInfo>()
                .Ignore(e => e.IP)  // Ignores the IP property. Instead, Host will contain the IP address.
                .HasKey(e => e.Key)
                ;

            modelBuilder.Entity<SessionHistory>()
                .HasKey(e => e.Key)
                ;
        }
    }
}

