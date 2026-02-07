using Spectre.Console.Cli;

namespace BitcoinCrawlerStats
{
    public class CrawlerCommand : AsyncCommand<CrawlerCommandLineSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, CrawlerCommandLineSettings settings, CancellationToken cancellationToken)
        {
            CrawlerEngine ce = new CrawlerEngine(context, settings, cancellationToken);

            return await ce.ExecuteAsync();
        }
    }
}
