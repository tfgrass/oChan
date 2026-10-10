using System;
using System.IO;
using System.Text;
using System.Threading;

namespace oChan.Persistence;

/// <summary>Single-writer atomic replacement within one filesystem directory.</summary>
internal static class AtomicJsonFile
{
    public static void WriteAllText(string filePath, string contents, bool backupCurrentFile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(filePath)
            ?? throw new ArgumentException("An absolute destination path is required.", nameof(filePath));
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(contents);
            using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // Cancellation must not report failure after a new snapshot has been committed.
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(filePath))
            {
                File.Move(temporaryPath, filePath);
            }
            else if (backupCurrentFile)
            {
                File.Replace(temporaryPath, filePath, filePath + ".bak");
            }
            else
            {
                // A recovery save must not replace the last valid backup with damaged data.
                PreserveFile(filePath);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, filePath, overwrite: true);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string PreserveFile(string filePath)
    {
        string preservedPath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        File.Copy(filePath, preservedPath, overwrite: false);
        return preservedPath;
    }
}
