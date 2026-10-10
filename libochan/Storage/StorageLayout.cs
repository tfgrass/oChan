using System;
using System.IO;

namespace oChan.Storage;

/// <summary>Creates portable paths from stable source identifiers, never from original upload names.</summary>
public static class StorageLayout
{
    public static string GetBoardDirectory(string root, string source, string board)
    {
        return CombineInsideRoot(root, ValidateSegment(source, nameof(source)), ValidateSegment(board, nameof(board)));
    }

    public static string GetThreadDirectory(string root, string source, string board, string thread)
    {
        return CombineInsideRoot(root, ValidateSegment(source, nameof(source)),
            ValidateSegment(board, nameof(board)), ValidateSegment(thread, nameof(thread)));
    }

    public static string GetMediaPath(string root, string source, string board, string thread, string mediaId, string ext)
    {
        string fileName = ValidateSegment(mediaId, nameof(mediaId)) + ValidateExtension(ext);
        return CombineInsideRoot(root, ValidateSegment(source, nameof(source)),
            ValidateSegment(board, nameof(board)), ValidateSegment(thread, nameof(thread)), fileName);
    }

    private static string ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException("A storage identifier must contain between 1 and 128 characters.", parameterName);
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_' && character != '-')
            {
                throw new ArgumentException("Storage identifiers may contain only ASCII letters, digits, underscores and hyphens.", parameterName);
            }
        }

        string upper = value.ToUpperInvariant();
        bool reservedDevice = upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal)
                || upper.StartsWith("LPT", StringComparison.Ordinal)) && upper[3] >= '1' && upper[3] <= '9');
        if (reservedDevice)
        {
            throw new ArgumentException("Storage identifiers cannot use reserved Windows device names.", parameterName);
        }

        return value;
    }

    private static string ValidateExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension) || extension.Length < 2 || extension.Length > 13 || extension[0] != '.')
        {
            throw new ArgumentException("A media extension must be a dot followed by 1 to 12 ASCII letters or digits.", nameof(extension));
        }

        for (int index = 1; index < extension.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(extension[index]))
            {
                throw new ArgumentException("A media extension must contain only ASCII letters or digits.", nameof(extension));
            }
        }

        return extension.ToLowerInvariant();
    }

    private static string CombineInsideRoot(string root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A download root is required.", nameof(root));
        }

        string absoluteRoot = Path.GetFullPath(root);
        string candidate = absoluteRoot;
        foreach (string segment in segments)
        {
            candidate = Path.Combine(candidate, segment);
        }

        candidate = Path.GetFullPath(candidate);
        string rootPrefix = Path.EndsInDirectorySeparator(absoluteRoot)
            ? absoluteRoot : absoluteRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootPrefix, comparison))
        {
            throw new ArgumentException("The media path must remain inside the download root.", nameof(root));
        }

        return candidate;
    }
}
