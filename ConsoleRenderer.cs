using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using System.Collections.Concurrent;
using System.Diagnostics;

using static BitcoinCrawlerStats.StringUtils;

namespace BitcoinCrawlerStats
{
    public class ConsoleRenderer
    {
        readonly CrawlerEngine _crawler;
        readonly Stopwatch _stopwatch;
        readonly CancellationToken _cancellationToken;
        readonly CrawlerCommandLineSettings _settings;

        public static ManualResetEventSlim RefreshEvent { get; } =  new ManualResetEventSlim(false);

        static int ConsoleWidth => AnsiConsole.Console.Profile.Width;

        object _renderLock = new object();

        UIMode _uiMode = UIMode.Main;

        public ConsoleRenderer(CrawlerEngine crawler, Stopwatch stopwatch,
                        CrawlerCommandLineSettings settings, CancellationToken cancellationToken)
        {
            _crawler = crawler;
            _stopwatch = stopwatch;
            _cancellationToken = cancellationToken;
            _settings = settings;
        }

        public void Start()
        {
            // Start the live display
            _ = Task.Run(() =>
            {
                AnsiConsole
                    .Live(CreateLayout(_crawler.LiveStatistics, _stopwatch!, _uiMode))
                    .Start(ctx =>
                    {
                        while (!_cancellationToken.IsCancellationRequested)
                        {
                            RefreshEvent.Wait();     // Wait until something changes
                            RefreshEvent.Reset();    // Prepare for next signal

                            lock (_renderLock)
                            {
                                if (_cancellationToken.IsCancellationRequested)
                                    break;

                                AnsiConsole.Console.Profile.Width = Console.WindowWidth;

                                // This runs on Spectre's rendering thread — safe to update UI
                                ctx.UpdateTarget(CreateLayout(_crawler.LiveStatistics, _stopwatch!, _uiMode));
                                ctx.Refresh();
                            }

                            try
                            {
                                Task.Delay(_settings.RefreshIntervalSeconds * 1000, _cancellationToken).Wait();
                            }
                            catch
                            {
                                break;
                            }
                        }
                    });
            });

            // Keyboard handler
            _ = Task.Run(() =>
            {
                while (!_cancellationToken.IsCancellationRequested)
                {
                    while (!Console.KeyAvailable && !_cancellationToken.IsCancellationRequested)
                        Task.Delay(100).Wait();

                    var key = Console.ReadKey(true);

                    switch (key.Key)
                    {
                        case ConsoleKey.D1:
                            if (_uiMode != UIMode.Main)
                            {
                                _uiMode = UIMode.Main;
                                RefreshEvent.Set();  // Signal UI to refresh
                            }
                            break;
                        case ConsoleKey.D2:
                            if (_uiMode != UIMode.Details)
                            {
                                _uiMode = UIMode.Details;
                                RefreshEvent.Set();  // Signal UI to refresh
                            }
                            break;
                        default:
                            break;
                    }
                }
            });
        }

        public void PrintStatistics()
        {
            AnsiConsole.Write(CreateLiveStatistics(_stopwatch, string.Empty));
            AnsiConsole.Write(CreateSecondaryStatistics(_stopwatch));
        }

        internal bool Lock(out object lockedObject)
        {
            lockedObject = null!;
            var gotLock = Monitor.TryEnter(_renderLock, TimeSpan.FromSeconds(2));
            if (!gotLock)
                return false;

            lockedObject = _renderLock;
            return true;
        }

        internal void RenderToRecorder(Recorder recorder)
        {
            foreach (UIMode uiMode in Enum.GetValues<UIMode>())
            {
                recorder.Write(CreateLayout(_crawler.LiveStatistics, _stopwatch!, uiMode));
            }
        }

        internal void Release(object lockedObject)
        {
            Monitor.Exit(lockedObject);
        }

        internal IRenderable CreateLayout(LiveStatistics stats, Stopwatch stopwatch, UIMode uiMode)
        {
            var rows = new List<IRenderable>();

            String buttonsStr = "";
            if (uiMode == UIMode.Main)
                buttonsStr = " [[[underline green]1[/]]] 2 ";
            else if (uiMode == UIMode.Details)
                buttonsStr = " 1 [[[underline green]2[/]]] ";

            buttonsStr = $"{buttonsStr} [[[yellow]g{ThisAssembly.Git.Sha}[/]]]";

            int w = (ConsoleWidth / 2) - 2;

            if (uiMode == UIMode.Main)
            {
                var topPanel = CreateLiveStatistics(stopwatch, buttonsStr);
                topPanel.Width = w;

                rows.Add(new Columns(topPanel, CreateLastBlocksTable("[bold green]Last Blocks[/]")));

                //CreateServiceStatsTable("[bold]Services[/]")

                rows.Add(new Columns(CreateUserAgentStatsTable(_crawler.UserAgentStats, "[bold green]Top User Agents[/]"),
                                     new Rows(
                                            new Markup("[bold green]Protocol statistics[/]").Centered(),
                                            new Text(""),
                                            CreateProtocolBreakdownChart(w),
                                            new Text(""),
                                            new Text(""),
                                            new Markup("[bold green]Node types[/]").Centered(),
                                            new Text(""),
                                            CreateNodeTypeBreakdownChart(w),
                                            new Text("")
                                        )));

                rows.Add(CreateSessionsTable("[bold green]Sessions[/]"));
            }
            else if (uiMode == UIMode.Details)
            {
                var grid = new Grid()
                    .AddColumn(new GridColumn().PadRight(4))
                    .AddColumn()
                    .AddRow("[bold yellow]Spamming Agents:[/] ", $"[green]{_crawler.SpammerUserAgentStats.Values.Sum(),10:N0}[/]");

                var topPanel = new Panel(grid)
                    .Header($"[bold blue]More Statistics {buttonsStr} [/]")
                    .Border(BoxBorder.Rounded)
                    .Padding(2, 1);

                topPanel.Width = (ConsoleWidth / 2) - 2;

                rows.Add(topPanel);

                rows.Add(new Columns(CreateUserAgentStatsTable(_crawler.ActiveUserAgentStats, $"[bold green]Active User Agents { (_settings.DisableEvaluation ? "(got handshake)" : "(basic evaluation)") }[/]"),
                                     CreateUserAgentStatsTable(_crawler.InactiveUserAgentStats, "[bold red]Inactive User Agents[/]")));

                rows.Add(CreateSecondaryStatistics(stopwatch));

                rows.Add(CreateLogTable("[bold]Log[/]"));
            }

            return new Rows(rows).Expand();
        }

        Panel CreateSecondaryStatistics(Stopwatch stopwatch)
        {
            var activeKnots = _crawler.AllSessionHistory.Where(p => p.Value.Active is true && p.Value.UserAgent != null && p.Value.UserAgent.Contains("/Knots:")).Count();
            var activeBip110Count = _crawler.AllSessionHistory.Where(p => p.Value.Active is true && p.Value.HasBip110).Count();
            var totalActive = _crawler.AllSessionHistory.Where(p => p.Value.Active is true).Count();

            var knotsPct = totalActive != 0 ? 100 * activeKnots / (double)totalActive : 0;
            var bipPct   = totalActive != 0 ? 100 * activeBip110Count / (double)totalActive : 0;

            var grid = new Grid()
                .AddColumn(new GridColumn().PadRight(4))
                .AddColumn()
                .AddRow("[bold yellow]Total active:[/] ", $"[green]{totalActive,10:N0}[/] peers")
                .AddRow("[bold yellow]Total inactive:[/] ", $"[red]{_crawler.InactiveUserAgentStats.Sum(p => p.Value),10:N0}[/] peers")
                .AddRow("[bold yellow]Total active Knots:[/] ", $"[green]{activeKnots,10:N0}[/] peers ({knotsPct,5:N2} %)")
                .AddRow("[bold yellow]Total active BIP-110:[/] ", $"[green]{activeBip110Count,10:N0}[/] peers ({bipPct,5:N2} %)");

            var panel = new Panel(grid)
                .Border(BoxBorder.Rounded)
                .Padding(2, 1);

            return panel;
        }

        Panel CreateLiveStatistics(Stopwatch stopwatch, String buttonsStr)
        {
            var stats = _crawler.LiveStatistics;

            DateTime oldestSession = DateTime.Now;
            if (_crawler.Sessions.Count > 0)
            {
                var list = _crawler.Sessions.Where(p => p.Value != null && !p.Value.Pinned).OrderBy(p => p.Value.Start).ToList();
                if (list.Count > 0)
                    oldestSession = list.FirstOrDefault().Value.Start;
            }

            var bip110Count = _crawler.ServiceStats.Where(p => p.Key == nameof(BitcoinSession.ServiceFlags.NODE_UASF_REDUCED_DATA)).FirstOrDefault().Value;

            var grid = new Grid()
                .AddColumn(new GridColumn().PadRight(4))
                .AddColumn()
                .AddRow("[bold yellow]Initial Peers:[/] ", $"[green]{_crawler.InitialPeers.Count,10:N0}[/] peers")
                .AddRow("[bold yellow]Collected:[/] ", $"[green]{_crawler.Collected.Count,10:N0}[/] peers")
                .AddRow("[bold yellow]Unvisited:[/] ", $"{_crawler.Unvisited.Count,10:N0} peers")
                .AddRow("[bold yellow]Visited:[/] ", $"[green]{_crawler.Visited.Count,10:N0}[/] peers")
                .AddRow("[bold yellow]Evaluated:[/] ", $"[green]{_crawler.Evaluated.Count(),10:N0}[/] peers")
                //.AddRow("[bold yellow]Unique user Agents:[/] ", $"[green]{_crawler.UserAgentStats.Count,10:N0}[/]")
                .AddRow("[bold yellow]Total user Agents:[/] ", $"[green]{_crawler.UserAgentStats.Values.Sum(),10:N0}[/]");

            if (!_settings.DisableTor)
                grid
                    //.AddRow("[bold yellow]Tor success:[/] ", $"[green]{stats.TorSuccess,10:N0}[/]")
                    .AddRow("[bold yellow]Tor errors:[/] ", $"[red]{stats.TorErrors,10:N0}[/]");

            grid.AddRow("[bold yellow]Conn. errors:[/] ", $"[red]{stats.ConnectionErrors,10:N0}[/]")
                //.AddRow("[bold yellow]Stream errors:[/] ", $"[red]{stats.StreamErrors,10:N0}[/]")
                .AddRow("[bold yellow]Active sessions:[/] ", $"[green]{_crawler.Sessions.Count,10:N0}[/]")
                .AddRow("[bold yellow]Elapsed:[/] ", $"[magenta]{stopwatch.Elapsed:d\\d\\ hh\\:mm\\:ss}[/]")
                //.AddRow("[bold yellow]FPS:[/]     ", $"[white]{stats.LastFps,10:F1}[/]")
                .AddRow("[bold yellow]Time:[/] ", $"{DateTime.Now.ToString("HH:mm:ss")}")
                .AddRow("[bold yellow]Max session age:[/] ", $"{(DateTime.Now - oldestSession).TotalSeconds,10:N0} s")
                ;

            return new Panel(grid)
                .Header($"[bold blue]Live Statistics {buttonsStr} [/]")
                .Border(BoxBorder.Rounded)
                .Padding(2, 1);
        }

        // New: 10-row × 3-column table
        static Table CreateUserAgentStatsTable(ConcurrentDictionary<string, int> uaStats, string title)
        {
            const int MAX_USER_AGENT_ROWS = 10;

            var uaWidth = Math.Max((ConsoleWidth) / 2 - 30, 10);

            var table = new Table()
                .Title(title)
                .Border(TableBorder.Rounded)
                .AddColumn("User agent", c => c.RightAligned().Width(uaWidth))
                .AddColumn("Count", c => c.RightAligned().Width(10))
                .AddColumn("Pct", c => c.RightAligned().Width(6));

            // Always show exactly 10 rows (newest on top)
            var sorted = uaStats.OrderByDescending(kv => kv.Value);
            var rows = sorted.Take(Math.Min(sorted.Count(), MAX_USER_AGENT_ROWS)).ToList();
            var total = uaStats.Values.Sum();
            for (int i = 0; i < MAX_USER_AGENT_ROWS; i++)
            {
                if (i < rows.Count)
                {
                    var row = rows[i];

                    table.AddRow(
                        $"[bold]{SafeSubstring(row.Key, Math.Max(row.Key.Length - uaWidth, 0))}[/]",
                        $"[yellow]{row.Value}[/]",
                        $"[green]{(row.Value * 100.0 / total):F2}%[/]"
                    );
                }
                else
                {
                    table.AddEmptyRow(); // Keeps table height fixed
                }
            }

            return table;
        }

        Table CreateProtocolStatsTable(string title)
        {
            const int MAX_PROTOCOL_ROWS = 4;

            var uaWidth = Math.Max(ConsoleWidth / 2 - 30, 10);

            var table = new Table()
                .Title(title)
                .Border(TableBorder.Rounded)
                .AddColumn("Id", c => c.RightAligned().Width(uaWidth))
                .AddColumn("Count", c => c.RightAligned().Width(12))
                .AddColumn("Pct", c => c.RightAligned().Width(6));

            // Always show exactly N rows (newest on top)
            var sorted = _crawler.ProtocolStats.OrderByDescending(kv => kv.Value);
            var rows = sorted.Take(Math.Min(sorted.Count(), MAX_PROTOCOL_ROWS)).ToList();
            var total = _crawler.ProtocolStats.Values.Sum();
            for (int i = 0; i < MAX_PROTOCOL_ROWS; i++)
            {
                if (i < rows.Count)
                {
                    var row = rows[i];
                    table.AddRow(
                        $"[bold]{row.Key}[/]",
                        $"[yellow]{row.Value}[/]",
                        $"[green]{(row.Value * 100.0 / total):F2}%[/]"
                    );
                }
                else
                {
                    table.AddEmptyRow(); // Keeps table height fixed
                }
            }

            return table;
        }

        BreakdownChart CreateProtocolBreakdownChart(int width)
        {
            var chart = new BreakdownChart()
                            .Width(width);

            var stats = _crawler.AllSessionHistory
                                .Where(p => p.Value.Connected is true)
                                .GroupBy(p => p.Value.NetworkId)
                                .Select(g => new {
                                    NetworkId = g.Key,
                                    NetworkIdStr = ((NetworkId)g.Key).ToString(),
                                    Count = g.Count()
                                })
                                .OrderByDescending( p => p.Count);

            int total = stats.Sum(p => p.Count);
            if (total != 0)
                chart = chart.UseValueFormatter((value, culture) => $"{value:N0} ({(100 * value / (double)total):N0}%)");

            var colors = new [] { Color.Green, Color.Blue, Color.Yellow, Color.Red };

            foreach (var item in stats)
                chart.AddItem(item.NetworkIdStr, item.Count, colors[item.NetworkId % 4]);

            return chart;
        }

        BreakdownChart CreateNodeTypeBreakdownChart(int width)
        {
            var chart = new BreakdownChart()
                            .Width(width);

            var stats = _crawler.AllSessionHistory
                                .Where(p => p.Value.Connected is true)
                                .GroupBy(p =>
                                    {
                                        var services = p.Value.Services;

                                        var ret = ((services & (ulong)BitcoinSession.ServiceFlags.NODE_NETWORK_LIMITED) != 0 ? 1 : 0)
                                               + (((services & (ulong)BitcoinSession.ServiceFlags.NODE_NETWORK) != 0 ? 1 : 0) << 1);   // Full node

                                        return ret == 2 ? 3 : ret; // hack: on rare occasions, nodes have NODE_NETWORK only. Go figure...
                                    }
                                )
                                .Select(g => new {
                                    Key = g.Key,
                                    Desc = (g.Key == 1 ? "Pruned" : (g.Key == 3 ? "Full" : "Other")),
                                    Count = g.Count()
                                })
                                .OrderByDescending(p => p.Count);

            int total = stats.Sum(p => p.Count);

            if (total != 0)
                chart = chart.UseValueFormatter((value, culture) => $"{value:N0} ({(100 * value / (double)total):N0}%)");

            var colors = new[] { Color.Blue, Color.Yellow, Color.Blue, Color.Green };

            foreach (var item in stats)
                chart.AddItem(item.Desc, item.Count, colors[item.Key % 4]);

            return chart;
        }

        Table CreateServiceStatsTable(string title)
        {
            const int MAX_SERVICE_ROWS = 4;

            var uaWidth = Math.Max(ConsoleWidth / 2 - 30, 10);

            var table = new Table()
                .Title(title)
                .Border(TableBorder.Rounded)
                .AddColumn("Flag", c => c.RightAligned().Width(uaWidth))
                .AddColumn("Count", c => c.RightAligned().Width(12));

            // Always show exactly N rows (newest on top)
            var sorted = _crawler.ServiceStats.OrderByDescending(kv => kv.Value);
            var rows = sorted.Take(Math.Min(sorted.Count(), MAX_SERVICE_ROWS)).ToList();

            for (int i = 0; i < MAX_SERVICE_ROWS; i++)
            {
                if (i < rows.Count)
                {
                    var row = rows[i];
                    table.AddRow(
                        $"[bold]{row.Key}[/]",
                        $"[yellow]{row.Value}[/]"
                    );
                }
                else
                {
                    table.AddEmptyRow(); // Keeps table height fixed
                }
            }

            return table;
        }

        Table CreateLastBlocksTable(string title)
        {
            var hashWidth = Math.Max((ConsoleWidth / 2) - 40, 4);

            var table = new Table()
                .Title(title)
                .Border(TableBorder.Rounded)
                .AddColumn("Hash", c => c.RightAligned().Width(hashWidth))
                .AddColumn("1st seen", c => c.Centered().Width(12))
                .AddColumn("Count", c => c.RightAligned().Width(12))
                //.AddColumn("Pct", c => c.LeftAligned().Width(7))
                ;

            const int BLOCK_ROWS = 8;

            // Always show exactly 10 rows (newest on top)
            var sorted = _crawler.BlocksAnnounced.OrderByDescending(kv => kv.Value.FirstSeen);
            var rows = sorted.Take(Math.Min(sorted.Count(), BLOCK_ROWS)).ToList();
            var total = _crawler.BlocksAnnounced.Select(p => p.Value.Count).Sum();
            for (int i = 0; i < BLOCK_ROWS; i++)
            {
                if (i < rows.Count)
                {
                    var row = rows[i];
                    table.AddRow(
                        $"[bold]{SafeSubstring(row.Key, row.Key.Length - hashWidth, hashWidth)}[/]",
                        $"{row.Value.FirstSeen.ToString("HH:mm:ss")}",
                        $"[yellow]{row.Value.Count}[/]"
                    //$"[green]{(row.Value.count * 100.0 / total):F2}%[/]"
                    );
                }
                else
                {
                    table.AddEmptyRow(); // Keeps table height fixed
                }
            }

            return table;
        }

        Table CreateSessionsTable(string title)
        {
            const int USER_AGENT_LENGTH = 38;

            var table = new Table()
                .Title(title)
                .Border(TableBorder.Rounded)
                .AddColumn("User agent", c => c.RightAligned().Width(USER_AGENT_LENGTH));

            if (_settings.ShowSessionBufferInfo)
            {
                table.AddColumn("Buffer Len", c => c.RightAligned().Width(12))
                     .AddColumn("Wanted Len", c => c.RightAligned().Width(12));
            }
            else
            {
                //table.AddColumn("GotVerack", c => c.Centered().Width(12))
                table.AddColumn("Start", c => c.RightAligned().Width(19))
                     .AddColumn("Buffer Pos", c => c.RightAligned().Width(10));
            }

            table.AddColumn("Proto", c => c.LeftAligned().Width(5))
                 .AddColumn("Addrs", c => c.RightAligned().Width(5))
                 .AddColumn("LastReceived", c => c.LeftAligned().Width(11))
                 .AddColumn("LastMsg", c => c.LeftAligned().Width(10))
                 ;

            const int ROWCOUNT = 10;
            var sorted = _crawler.Sessions.Where(p => !String.IsNullOrEmpty(p.Value.UserAgent)).OrderBy(kv => kv.Value?.Start ?? DateTime.Now);   // order by session age, oldest first
            //var sorted = Sessions.OrderByDescending(kv => kv.Value?.Addresses ?? 0);
            var rows = sorted.Take(Math.Min(sorted.Count(), ROWCOUNT)).ToList();
            for (int i = 0; i < ROWCOUNT; i++)
            {
                if (i < rows.Count)
                {
                    var row = rows[i];
                    var si = row.Value.SessionInfo;
                    var rcvage = (DateTime.Now - si.LastReceive);
                    var ageStr = si.LastReceive != DateTime.MinValue ? $"{rcvage.TotalSeconds:N0} sec ago" : "";
                    long? bufferPos = si.MessageBuffer != null && si.MessageBuffer.CanRead ? si.MessageBuffer.Position : null;
                    long? bufferLen = si.MessageBuffer != null && si.MessageBuffer.CanRead ? si.MessageBuffer.Length : null;

                    var columns = new List<String>();
                    columns.Add($"[bold]{(si.UserAgent != null ? SafeSubstring(si.UserAgent, Math.Max(si.UserAgent.Length - USER_AGENT_LENGTH, 0)) : String.Empty)}[/]");

                    if (_settings.ShowSessionBufferInfo)
                    {
                        columns.Add($"{bufferLen}");
                        columns.Add($"{si.WantedLength:N0}");
                    }
                    else
                    {
                        columns.Add($"[yellow]{si.Start.ToString("dd/MM/yyyy HH:mm:ss")}[/]");
                        //columns.Add($"[{(row.Value.GotVerack ? "green":"red")}]{row.Value.GotVerack}[/]");
                        columns.Add($"[{(bufferPos == 0 ? "green" : "red")}]{bufferPos}[/]");
                    }

                    columns.Add($"[{(si.NetworkId == NetworkId.IPv4 || si.NetworkId == NetworkId.IPv6 ? "yellow" : "green")}]{si.NetworkId}[/]");
                    columns.Add($"[green]{si.Addresses:N0}[/]");
                    columns.Add($"[green]{ageStr}[/]");
                    columns.Add($"[green]{si.LastMessage}[/]");

                    table.AddRow(columns.ToArray());
                }
                else
                {
                    table.AddEmptyRow(); // Keeps table height fixed
                }
            }

            return table;
        }

        Table CreateLogTable(string title)
        {
            const int LOG_WIDTH = 110;

            var table = new Table()
                .Title(title)
                .Border(TableBorder.Rounded)
                .AddColumn("Message", c => c.LeftAligned().Width(LOG_WIDTH))
                ;

            var sorted = CrawlerEngine.LogQueue.ToArray();   // order by session age, oldest first
            //var sorted = Sessions.OrderByDescending(kv => kv.Value?.Addresses ?? 0);
            var rows = sorted.Take(Math.Min(sorted.Count(), CrawlerEngine.MAX_VISIBLE_LOG_ENTRIES)).ToList();
            for (int i = 0; i < CrawlerEngine.MAX_VISIBLE_LOG_ENTRIES; i++)
            {
                if (i < rows.Count)
                {
                    var row = rows[i];
                    table.AddRow(
                        row ?? ""
                    );
                }
                else
                {
                    table.AddEmptyRow(); // Keeps table height fixed
                }
            }

            return table;
        }
    }

    public enum UIMode
    {
        Main = 1,
        Details = 2,
    }
}
