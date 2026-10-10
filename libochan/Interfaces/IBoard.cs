using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel;
using oChan.Downloader;
using oChan.Http;

namespace oChan.Interfaces
{
    /// <summary>
    /// Represents a specific board within an imageboard platform (e.g., "/g/" on 4chan).
    /// </summary>
    public interface IBoard : INotifyPropertyChanged
    {
        /// <summary>
        /// Gets the parent imageboard.
        /// </summary>
        IImageBoard ImageBoard { get; }

        /// <summary>
        /// Gets the board code (e.g., "g" for /g/).
        /// </summary>
        string BoardCode { get; }

        /// <summary>
        /// Gets the name of the board (e.g., "Technology").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets a user-friendly name for the board.
        /// </summary>
        string NiceName { get; }

        /// <summary>
        /// Gets the URI of the board.
        /// </summary>
        Uri BoardUri { get; }

        /// <summary>The current HTTP admission, cooldown, or sending phase; null when no request is active.</summary>
        RequestPolicyProgress? RequestProgress { get; }

        /// <summary>
        /// Fetches the threads on the board.
        /// </summary>
        /// <param name="cancellationToken">Cancels metadata and catalog requests.</param>
        /// <returns>A task representing the asynchronous operation, containing a collection of <see cref="IThread"/> instances.</returns>
        Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default);


        /// <summary>
        /// Starts monitoring the board for new threads.
        /// </summary>
        /// <param name="intervalInSeconds">The interval in seconds between checks.</param>
        void StartMonitoring(int intervalInSeconds);

        /// <summary>Changes the delay for the next waiting period without interrupting current work.</summary>
        void UpdateMonitoringInterval(TimeSpan interval);

        /// <summary>
        /// Stops monitoring the board.
        /// </summary>
        void StopMonitoring();

        /// <summary>
        /// Stops future checks, cancels the current request, suppresses late discoveries,
        /// and awaits the current check.
        /// </summary>
        Task StopMonitoringAsync();

        /// <summary>
        /// Reports each thread in a fetched catalog. The session owns admission and deduplication.
        /// </summary>
        IDisposable RegisterDiscoverySink(Func<IThread, CancellationToken, ValueTask> sink);

        BoardSnapshot GetSnapshot(string downloadDirectory);
        FailureInfo? LastCheckFailure { get; }
    }
}

