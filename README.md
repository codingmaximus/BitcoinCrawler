# Bitcoin Crawler

This software recursively connects to peers on the Bitcoin network, collecting statistics on User Agent information.

By default, this program performs a basic evaluation to distinguish active nodes from malfunctioning ones. 
This basic evaluation consists on waiting for each peer to broadcast information about at least two new valid blocks ("inv" message). 
By valid blocks, we mean blocks broadcast by at least half of the sessions active. 
It also detects collects statistics on nodes sending "inv" SPAM. (Unfortunately we need that...)

NOTE: when basic evaluation is enabled, crawling the whole network takes around a week(!). By using option `--disable-evaluation`, crawling is much faster.

If running on a Linux environment (or equivalent) use "screen" to let program running on the background.

## Persistence

BitcoinCrawler uses sqlite to keep data between sessions. It writes to file "crawler.db" on the same folder as the executable. This can be overriden by command line option (see below).

When you're done with crawling (e.g.: list of unvisited peers is almost finished), press CTRL+C or CTRL+Break to interrupt.

If you want to start fresh (e.g.: make another run), rename "crawler.db" to something else and start BitcoinCrawler again.

## User Interfaces

### Console

Hotkeys:
	1 - Show live stats page (default)
	2 - Show live statistics second page (Active, Inactive, Log)

### Web

Can be enabled with option `--enable-http-server`. 
It has exactly the same output as the console, with fixed-size font. Completely read-only. But at least allows you to check things up from your couch...

## Building instructions

### Debug
dotnet build BitcoinCrawlerStats.csproj

### Release
dotnet build -c Release BitcoinCrawlerStats.csproj

## Running

USAGE:
    dotnet BitcoinCrawlerStats.dll [OPTIONS]

OPTIONS:
                                      DEFAULT
    -h, --help                                                Prints help information
        --disable-ip                                          Disables connecting to IPv4/IPv6 addresses
        --disable-tor                                         Disables connecting to Tor v3 addresses
        --tor-proxy-host              127.0.0.1               Tor proxy host. Required to connect to .onion addresses
        --tor-proxy-port              9050                    Tor proxy port. Required to connect to .onion addresses
        --single-seed-host                                    For debugging purposes. Specifies the single seed host to get peers from. e.g.:
                                                              your node address
        --single-seed-port            8333                    For debugging purposes. Specifies the single seed port to get peers from. e.g.:
                                                              your node port
        --user-agent                  /BitcoinCrawler:1.0/    User agent string
        --max-sessions                200                     Specifies the maximum number of active sessions
        --max-tor-connect-attempts    10                      Specifies the maximum number of simultaneous Tor
                                                              connection attempts
        --refresh-interval            1                       Specifies UI refresh interval in seconds
        --enable-http-server                                  Specifies whether the HTTP server should be started
                                                              (disabled by default)
        --http-server-address         localhost:5050          Specifies the HTTP server address (host:port)
        --db-path                     crawler.db              Specifies the path to the database file. (sqlite)
        --debug-parse                                         Enable debug of message parsing
        --verbose                                             Enable verbose mode
        --show-session-buffer-info                            Show buffer information in session panel
        --disable-evaluation                                  Disables peer evaluation about new block broadcast. Makes
                                                              crawler much faster
