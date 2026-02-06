using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;

namespace BitcoinCrawlerStats
{
    public class MiniHttpServer
    {
        readonly String _address;
        readonly CrawlerEngine _crawler;

        static object _classRecorderLock = new object();

        public MiniHttpServer(string address, CrawlerEngine crawler)
        {
            _address = address ?? throw new ArgumentNullException(nameof(address));
            _crawler = crawler ?? throw new ArgumentNullException(nameof(crawler));
        }

        public async Task Run()
        {
            // Program.cs
            var builder = WebApplication.CreateSlimBuilder();   // ← lightweight version

            // Optional: choose exactly one port (no random port)
            builder.WebHost.UseUrls($"http://{_address}");

            // Optional: disable all logging noise
            builder.Logging.ClearProviders();

            var app = builder.Build();

            // ── Your endpoints ────────────────────────────────────────────────

            app.MapGet("/", () =>
                Results.Content(
                    SafeRenderCrawlerOutputInHtml(),
                    "text/html"
                )
            );

            // ───────────────────────────────────────────────────────────────────

            Console.WriteLine($"Starting HTTP server on http://{_address}");

            await app.RunAsync();
        }

        string SafeRenderCrawlerOutputInHtml()
        {
            var interval = TimeSpan.FromSeconds(2);

            bool locked = false;
            String stage = String.Empty;
            try
            {
                stage = "acquiring recorder lock";
                locked = Monitor.TryEnter(_classRecorderLock, interval);

                var taskToComplete = RenderCrawlerOutputInHtml();
                try
                {
                    stage = "waiting for Crawler to render";
                    return taskToComplete.WaitAsync(interval).Result;
                }
                catch (Exception ex)
                {
                    return $"Error rendering crawler output: {ex.Message}";
                }
            }
            finally
            {
                if (locked)
                    Monitor.Exit(_classRecorderLock);
            }
        }

        async Task<string> RenderCrawlerOutputInHtml()
        {
            object rendererLock = null!;
            Recorder? recorder = null;
            try
            {
                if (!_crawler.Renderer!.Lock(out rendererLock))
                    throw new Exception("Unable to get renderer lock");

                //var console = AnsiConsole.Console;    // DISABLED: affects main console
                var console = new TestConsole();
                const int CONSOLE_WIDTH = 180;
                console.Width(CONSOLE_WIDTH);
                AnsiConsole.Console.Profile.Width = CONSOLE_WIDTH;
                recorder = new Recorder(console);
                _crawler.Renderer!.RenderToRecorder(recorder);

                var sb = new StringBuilder();
                sb.Append("<!DOCTYPE html>");
                sb.Append("<html lang=\"en\">");
                sb.Append("<head>");
                sb.Append("  <meta charset=\"UTF-8\">");
                sb.Append("  <title>Crawler Statistics</title>");
                sb.Append("  <style>");
                sb.Append("    html, body {");
                sb.Append("      background-color: black;");
                sb.Append("    }");
                sb.Append("    pre { ");
                sb.Append("      font-family: 'Consolas', 'Courier New', monospace; ");
                sb.Append("      background: #1e1e1e; ");
                sb.Append("      color: #d4d4d4; ");
                sb.Append("      padding: 1rem; ");
                sb.Append("      border-radius: 6px; ");
                sb.Append("      overflow-x: auto;");
                sb.Append("    }");
                sb.Append("  </style>");
                sb.Append("</head>");
                sb.Append("<body>");

                sb.Append(recorder.ExportHtml());

                sb.Append("</body>");
                sb.Append("</html>");

                await Task.FromResult(0);

                return sb.ToString();
            }
            finally
            {
                recorder?.Clear();
                recorder?.Dispose();

                AnsiConsole.Console.Profile.Width = Console.WindowWidth;

                if (rendererLock != null)
                    _crawler.Renderer!.Release(rendererLock);
            }
        }
    }
}
