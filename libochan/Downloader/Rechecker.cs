namespace oChan.Downloader
{
    using System;
    using System.Collections.Generic;
    using System.Timers;
    using oChan.Interfaces;
    using Serilog;

    public class Rechecker
    {
        private readonly Timer _recheckTimer;
        private readonly List<IThread> _threadsToMonitor;

        public Rechecker(List<IThread> threads, int intervalInSeconds)
        {
            _threadsToMonitor = threads ?? throw new ArgumentNullException(nameof(threads));

            // Set up a timer with the specified interval in seconds
            _recheckTimer = new Timer(intervalInSeconds * 1000); // Convert seconds to milliseconds
            _recheckTimer.Elapsed += OnRecheckEvent;
            _recheckTimer.AutoReset = true; // Recheck periodically
            _recheckTimer.Enabled = true; // Start the timer

            Log.Information("Rechecker initialized with interval: {Interval} seconds", intervalInSeconds);
        }

        // Event triggered on timer interval
        private async void OnRecheckEvent(Object source, ElapsedEventArgs e)
        {
            Log.Information("Rechecking threads...");

            foreach (IThread thread in _threadsToMonitor)
            {
                try
                {
                    DownloadQueue queue = new DownloadQueue(5, 1024 * 1024); // Example queue for rechecking
                    await thread.checkThreadAsync(queue);
                    Log.Information("Rechecked thread {ThreadId}", thread.ThreadId);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error rechecking thread {ThreadId}", thread.ThreadId);
                }
            }
        }

        // Stop the rechecking process when necessary
        public void StopRechecking()
        {
            _recheckTimer.Stop();
            Log.Information("Rechecker stopped.");
        }
    }
}
