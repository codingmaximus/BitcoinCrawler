# DISCLAIMER

DISCLAIMER OF WARRANTIES
YOU ACKNOWLEDGE AND AGREE THAT THE SOFTWARE IS PROVIDED TO YOU ON AN "AS IS" BASIS.
THE LICENSOR DISCLAIMS ANY AND ALL REPRESENTATIONS AND WARRANTIES, EXPRESS OR IMPLIED
INCLUDING (WITHOUT LIMITATION) ANY IMPLIED WARRANTIES OF MERCHANTABILITY, OR HARDWARE
OR SOFTWARE COMPATIBILITY, OR FITNESS FOR A PARTICULAR PURPOSE OR USE, INCLUDING YOUR
PARTICULAR BUSINESS OR INTENDED USE, OR OF THE SOFTWARE'S RELIABILITY, PERFORMANCE OR
CONTINUED AVAILABILITY. THE LICENSOR DOES NOT REPRESENT OR WARRANT THAT THE
SOFTWARE OR CALCULATIONS OR PRINTS OR EXPORT DATA MADE THEREOF WILL BE FREE FROM
VIRUSES, MALWARE, TROJAN HORSES OR ANY OTHER DEFECTS OR ERRORS AND THAT ANY SUCH
EFFECTS OR ERRORS WILL BE CORRECTED, OR THAT IT WILL OPERATE WITHOUT INTERRUPTION.
YOU AGREE THAT YOU ARE SOLELY RESPONSIBLE FOR ALL COSTS AND EXPENSES ASSOCIATED
WITH RECTIFICATION, REPAIR OR DAMAGE CAUSED BY SUCH DEFECTS, ERRORS OR INTERRUPTIONS.
FURTHER, THE LICENSOR DOES NOT REPRESENT AND WARRANT THAT THE SOFTWARE DOES NOT
INFRINGE THE INTELLECTUAL PROPERTY RIGHT OF ANY OTHER PERSON. YOU ACCEPT
RESPONSIBILITY TO VERIFY THAT THE SOFTWARE MEETS YOUR SPECIFIC REQUIREMENTS.

# Bitcoin Crawler

This software recursively connects to peers on the Bitcoin network, collecting statistics on User Agent information.

By default, this program performs a basic evaluation to distinguish active nodes from malfunctioning ones. 
This basic evaluation consists on waiting for each peer to broadcast information about at least two new valid blocks ("inv" message). 
By valid blocks, we mean blocks broadcast by at least half of the sessions active. 
It also detects collects statistics on nodes sending "inv" SPAM. (Unfortunately we need that...)

NOTE: when basic evaluation is enabled, crawling the whole network takes around a week(!). By using option `--disable-evaluation`, crawling is much faster.

If running on a Linux environment (or equivalent) use "screen" to let program running on the background.

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

    # .NET 8
	dotnet build BitcoinCrawlerStats.sln

	# .NET 10
	dotnet build BitcoinCrawlerStats.NET10.sln

### Release

    # .NET 8
	dotnet build -c Release BitcoinCrawlerStats.sln

	# .NET 10
	dotnet build -c Release BitcoinCrawlerStats.NET10.sln

## Running

USAGE:

	dotnet BitcoinCrawlerStats.dll [OPTIONS]

OPTIONS:

	--help                                                Prints help information
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

## Persistence

BitcoinCrawler uses sqlite to keep data between sessions. It writes to file "crawler.db" in the same folder as the executable. This can be overriden by command line option (see below).

When you're done with crawling (e.g.: list of unvisited peers is almost finished), press CTRL+C or CTRL+Break to interrupt.
Note: If you want to start fresh (e.g.: make another run), rename "crawler.db" to something else and start BitcoinCrawler again.

### Tables

| Name               | Purpose                                               |
| ------------------ | ----------------------------------------------------- |
| UserAgents         | User Agent statistics (user agent, count)             |
| Unvisited          | List of unvisited peer addresses                      |
| Evaluated          | List of evaluated peer addresses                      |
| ActiveUserAgents   | Active client statistics (user agent, count)          |
| InactiveUserAgents | Inactive client statistics (user agent, count)        |
| SessionHistory     | Information about each session history                |
|                    | (network id, verack indicator, connection error, etc) |
| ProtocolStats      | Protocol statistics (network id, count)               |
| SpammerUserAgents  | Spammer client statistics (user agent, count)         |

### Custom SQL queries

Use `sqlite3 crawler.db` to inspect tables and perform custom queries.

Examples:

Get node count by User Agent string:

	select substr(id || '                          ', 1, 36),substr('     ' || CAST(round(total/24017.0,3)*100 as TEXT) || '%',-5,5) from (select Id,sum(count) total from ActiveUseragents group by Id) where total > 100 order by total desc;

Get number of Knots nodes:

	select sum(count),sum(count)/(select sum(count)/1.0 from ActiveUserAgents) from ActiveUseragents where Id like '%/Knots%';

Get number of nodes supporting BIP-110 (in the User Agent string):

	select sum(count),sum(count)/(select sum(count)/1.0 from ActiveUserAgents) from ActiveUseragents where Id like '%BIP%110%';


# License

MIT License
