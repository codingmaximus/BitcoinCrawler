using Spectre.Console.Cli;

namespace BitcoinCrawlerStats
{
    internal class Program
    {
        static CancellationTokenSource _cancTokenSource = new CancellationTokenSource();

        static async Task Main(string[] args)
        {
            Console.CancelKeyPress += (o, ea) => {
                _cancTokenSource.Cancel();
                ea.Cancel = true;
            };

            var app = new CommandApp<CrawlerCommand>();
            await app.RunAsync(args, _cancTokenSource.Token);
        }
    }
}
