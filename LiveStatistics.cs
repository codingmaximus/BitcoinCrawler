namespace BitcoinCrawlerStats
{
    public class LiveStatistics
    {
        public long ConnectionErrors;
        public long StreamErrors;

        public double LastFps => CalculateFps();

        private long _frameCount;
        private DateTime _lastFpsUpdate = DateTime.UtcNow;

        public long TorSuccess;
        public long TorErrors;

        public long I2pSuccess;
        public long I2pErrors;

        public double CalculateFps()
        {
            var now = DateTime.UtcNow;
            Interlocked.Increment(ref _frameCount);
            var elapsed = (now - _lastFpsUpdate).TotalSeconds;

            if (elapsed >= 1.0)
            {
                var fps = _frameCount / elapsed;
                Interlocked.Exchange(ref _frameCount, 0);
                _lastFpsUpdate = now;
                return fps;
            }

            return 0; // Will show increasing value
        }
    }
}
