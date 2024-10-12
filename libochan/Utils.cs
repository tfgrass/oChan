namespace oChan;

using System;
using System.IO;

public static class Utils
{
    public static string ToHumanReadableSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
public static class PathSanitizer
{
    public static string SanitizePath(string path)
    {
        // Remove invalid characters
        char[] invalidChars = Path.GetInvalidPathChars();
        foreach (char c in invalidChars)
        {
            path = path.Replace(c.ToString(), "");
        }

        return path;
    }
}

