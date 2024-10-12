namespace oChan.Boards.Base;

using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;
using System.Timers;

public abstract class BaseBoard : IBoard
{
    public abstract IImageBoard ImageBoard { get; }
    public abstract string BoardCode { get; }
    public abstract string Name { get; }
    public abstract string NiceName { get; }
    public abstract Uri BoardUri { get; }

    public virtual async Task<IEnumerable<IThread>> GetThreadsAsync()
    {
        Log.Debug("Fetching threads for board {BoardCode}", BoardCode);
        // Implement fetching logic
        return await Task.FromResult(new List<IThread>());
    }

    public virtual async Task ArchiveAsync(ArchiveOptions options)
    {
        Log.Information("Archiving board {BoardCode} with options {Options}", BoardCode, options);
        // Implement archiving logic
        await Task.CompletedTask;
    }
    // Timer for monitoring
    protected Timer _monitoringTimer;
    protected int _monitoringIntervalInSeconds;

    // HashSet to keep track of known thread IDs
    protected HashSet<string> _knownThreadIds = new HashSet<string>();

    public event EventHandler<ThreadEventArgs> ThreadDiscovered;

    public virtual void StartMonitoring(int intervalInSeconds)
    {
        if (_monitoringTimer == null)
        {
            _monitoringIntervalInSeconds = intervalInSeconds;

            _monitoringTimer = new Timer(_monitoringIntervalInSeconds * 1000);
            _monitoringTimer.Elapsed += OnMonitoringEvent;
            _monitoringTimer.AutoReset = true;
            _monitoringTimer.Enabled = true;

            Log.Information("Started monitoring board {BoardCode} with interval {Interval}s", BoardCode, intervalInSeconds);

            // Start the initial check
            Task.Run(async () => await CheckForNewThreadsAsync());
        }
        else
        {
            Log.Warning("Monitoring already started for board {BoardCode}", BoardCode);
        }
    }

    public virtual void StopMonitoring()
    {
        if (_monitoringTimer != null)
        {
            _monitoringTimer.Stop();
            _monitoringTimer.Dispose();
            _monitoringTimer = null;
            Log.Information("Stopped monitoring for board {BoardCode}", BoardCode);
        }
        else
        {
            Log.Warning("Monitoring not started for board {BoardCode}", BoardCode);
        }
    }

    protected virtual async void OnMonitoringEvent(Object source, ElapsedEventArgs e)
    {
        await CheckForNewThreadsAsync();
    }

    protected virtual async Task CheckForNewThreadsAsync()
    {
        try
        {
            Log.Debug("Checking for new threads on board {BoardCode}", BoardCode);

            IEnumerable<IThread> threads = await GetThreadsAsync();

            foreach (IThread thread in threads)
            {
                if (!_knownThreadIds.Contains(thread.ThreadId))
                {
                    _knownThreadIds.Add(thread.ThreadId);

                    // Raise the event
                    ThreadDiscovered?.Invoke(this, new ThreadEventArgs(thread));

                    Log.Information("New thread discovered: {ThreadId} on board {BoardCode}", thread.ThreadId, BoardCode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking for new threads on board {BoardCode}", BoardCode);
        }
    }
}
