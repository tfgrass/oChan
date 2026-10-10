using System;
using System.IO;

namespace oChan.Persistence;

/// <summary>Owns exclusive write access to one profile for its entire lifetime.</summary>
public sealed class ProfileLease : IDisposable
{
    private readonly object _sync = new object();
    private FileStream? _lockStream;

    public string ProfileDirectory { get; }

    private ProfileLease(string profileDirectory, FileStream lockStream)
    {
        ProfileDirectory = profileDirectory;
        _lockStream = lockStream;
    }

    /// <summary>Explicitly creates and locks the profile; independent profiles can coexist.</summary>
    public static ProfileLease Acquire(string profileDirectory)
    {
        string directory = NormalizeDirectory(profileDirectory);
        Directory.CreateDirectory(directory);
        string lockPath = Path.Combine(directory, ".ochan-profile.lock");

        try
        {
            FileStream stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new ProfileLease(directory, stream);
        }
        catch (IOException ex)
        {
            throw new IOException($"The profile '{directory}' could not be locked. Another oChan instance may already be using it.", ex);
        }
    }

    internal void EnsureMatches(string profileDirectory)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(ProfileDirectory, NormalizeDirectory(profileDirectory), comparison))
            throw new ArgumentException("The profile lease belongs to a different directory.", nameof(profileDirectory));

        RunExclusive(() => true);
    }

    internal T RunExclusive<T>(Func<T> operation)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_lockStream == null, this);
            return operation();
        }
    }

    internal static string NormalizeDirectory(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _lockStream?.Dispose();
            _lockStream = null;
        }

        // Keep the lock file: deleting it can let another process lock a different inode.
    }
}
