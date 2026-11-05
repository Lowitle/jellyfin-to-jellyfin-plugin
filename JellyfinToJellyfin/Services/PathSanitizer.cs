using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JellyfinToJellyfin.Services;

/// <summary>
/// Produces filesystem safe names from remote media titles.
/// </summary>
public static class PathSanitizer
{
    private static readonly HashSet<char> Invalid =
        new("<>\":/\\|?*".ToCharArray().Concat(Path.GetInvalidFileNameChars()));

    /// <summary>
    /// Sanitizes a media title into a safe file/folder name.
    /// </summary>
    public static string Sanitize(string? name, string fallback = "Unknown")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var sb = new StringBuilder(name!.Length);
        foreach (var ch in name)
        {
            if (Invalid.Contains(ch) || char.IsControl(ch))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var result = Regex.Replace(sb.ToString(), "\\s+", " ").Trim().TrimEnd('.', ' ');
        if (result.Length == 0)
        {
            result = fallback;
        }

        if (result.Length > 120)
        {
            result = result.Substring(0, 120).TrimEnd('.', ' ');
        }

        return result;
    }

    /// <summary>
    /// Compares two filesystem paths in a platform tolerant way.
    /// </summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            var na = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var nb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(na, nb, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
