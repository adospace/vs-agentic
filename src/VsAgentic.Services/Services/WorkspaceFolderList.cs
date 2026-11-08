using System.IO;

namespace VsAgentic.Services.Services;

/// <summary>
/// Turns the folders of a workspace's projects into the short list of folders
/// the agent needs besides its working directory.
/// </summary>
public static class WorkspaceFolderList
{
    /// <summary>
    /// The folders in <paramref name="folders"/> that lie outside
    /// <paramref name="root"/>, without duplicates and without a folder that
    /// another folder in the list already contains.
    /// </summary>
    /// <remarks>
    /// Left out: folders that do not exist; network paths, which the Claude
    /// CLI ignores (measured with 2.1.319); and drive roots, so that a project
    /// file at <c>C:\</c> does not open the whole drive.
    /// </remarks>
    public static IReadOnlyList<string> OutsideRoot(string root, IEnumerable<string?> folders)
    {
        var normalizedRoot = Normalize(root);

        var candidates = new List<string>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;

            var path = Normalize(folder!);
            if (path is null) continue;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            if (string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase)) continue;
            if (normalizedRoot is not null && IsSameOrInside(path, normalizedRoot)) continue;
            if (!Directory.Exists(path)) continue;

            candidates.Add(path);
        }

        // Shortest first, so a parent is kept before any folder inside it.
        var result = new List<string>();
        foreach (var path in candidates.OrderBy(p => p.Length))
        {
            if (result.Any(kept => IsSameOrInside(path, kept))) continue;
            result.Add(path);
        }

        return result;
    }

    private static string? Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim());
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // "C:\" trims to "C:", which GetFullPath would read as the current
            // folder on that drive.
            return trimmed.EndsWith(":", StringComparison.Ordinal) ? full : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return true;

        var prefix = folder.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? folder
            : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
