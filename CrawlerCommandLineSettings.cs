using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats
{
    public class CrawlerCommandLineSettings : CommandSettings
    {
        [CommandOption("--disable-ip")]
        [Description("Disables connecting to IPv4/IPv6 addresses")]
        [DefaultValue(false)]
        public bool DisableIP { get; init; } = true;

        [CommandOption("--enable-tor")]
        [Description("Enables connecting to Tor v3 addresses")]
        [DefaultValue(false)]
        public bool EnableTor { get; init; }

        [CommandOption("--enable-i2p")]
        [Description("Enables connecting to I2P addresses")]
        [DefaultValue(false)]
        public bool EnableI2P { get; init; }

        [CommandOption("--tor-proxy-host")]
        [Description("Tor proxy host. Required to connect to .onion addresses")]
        [DefaultValue("127.0.0.1")]
        public String? TorProxyHost { get; init; }

        [CommandOption("--tor-proxy-port")]
        [Description("Tor proxy port. Required to connect to .onion addresses")]
        [DefaultValue(9050)]
        public int TorProxyPort { get; init; }

        [CommandOption("--sam-host")]
        [Description("SAM host. Required to connect to I2P addresses")]
        [DefaultValue("127.0.0.1")]
        public String? SamHost { get; init; }

        [CommandOption("--sam-port")]
        [Description("SAM port. Required to connect to I2P addresses")]
        [DefaultValue(7656)]
        public int SamPort { get; init; }

        [CommandOption("--single-seed-host")]
        [Description("For debugging purposes. Specifies the single seed host to get peers from. e.g.: your node address")]
        public String? SingleSeedHost { get; init; }

        [CommandOption("--single-seed-port")]
        [Description("For debugging purposes. Specifies the single seed port to get peers from. e.g.: your node port")]
        [DefaultValue(CrawlerEngine.BitcoinPort)]
        public int SingleSeedPort { get; init; }

        [CommandOption("--user-agent")]
        [Description("User agent string.")]
        [DefaultValue("/BitcoinCrawler:1.0/")]
        public String? UserAgent { get; init; }

        [CommandOption("--max-sessions")]
        [Description("Specifies the maximum number of active sessions")]
        [DefaultValue(CrawlerEngine.MAX_ACTIVE_SESSIONS)]
        public int MaxActiveSessions { get; init; }

        [CommandOption("--max-tor-connect-attempts")]
        [Description("Specifies the maximum number of simultaneous Tor connection attempts")]
        [DefaultValue(CrawlerEngine.MAX_TOR_SIMULTANEOUS_CONNECT)]
        public int MaxTorSimultaneousConnects { get; init; }

        [CommandOption("--refresh-interval")]
        [Description("Specifies console UI refresh interval in seconds")]
        [DefaultValue(1)]
        public int RefreshIntervalSeconds { get; init; }

        [CommandOption("--enable-http-server")]
        [Description("Specifies whether the HTTP server should be started (disabled by default)")]
        [DefaultValue(false)]
        public bool EnableHttpServer { get; init; }

        [CommandOption("--http-server-address")]
        [Description("Specifies the HTTP server address (host:port)")]
        [DefaultValue("localhost:5050")]
        public String? HttpServerAddress { get; init; }

        [CommandOption("--db-path")]
        [Description("Specifies the path to the database file. (sqlite)")]
        [DefaultValue("crawler.db")]
        public String? DbFilePath { get; init; }

        [CommandOption("--debug-parse")]
        [Description("Enable debug of message parsing")]
        [DefaultValue(false)]
        public bool DebugParse { get; init; } = false;

        [CommandOption("--verbose")]
        [Description("Enable verbose mode")]
        public bool Verbose { get; init; } = false;

        [CommandOption("--show-session-buffer-info")]
        [Description("Show buffer information in session panel")]
        [DefaultValue(false)]
        public bool ShowSessionBufferInfo { get; init; } = false;

        [CommandOption("--disable-evaluation")]
        [Description("Disables peer evaluation of new block broadcasts. Makes crawler much faster")]
        [DefaultValue(false)]
        public bool DisableEvaluation { get; init; }

        [CommandOption("--disable-console-refresh")]
        [Description("Disables console refresh. Improves performance on slower systems")]
        [DefaultValue(false)]
        public bool DisableConsoleRefresh { get; init; }
    }
}
