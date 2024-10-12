namespace oChan.Boards.Base;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using oChan.Downloader;
using oChan.Interfaces;
using Serilog;

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
}
