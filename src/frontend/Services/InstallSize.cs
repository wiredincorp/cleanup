namespace Cleanup.Services;

/// <summary>
/// Adds up an install folder when the installer did not record a size.
/// Shared roots such as Program Files itself are never walked.
/// </summary>
internal static class InstallSize
{
    public static ulong? Measure(string? location, string? executable, CancellationToken cancel)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        bounded.CancelAfter(TimeSpan.FromMinutes(3));
        cancel = bounded.Token;
        cancel.ThrowIfCancellationRequested();
        if (ExistingFile(location) is string listedFile)
        {
            return Length(listedFile);
        }
        if (SpecificDirectory(location) is string folder)
        {
            return Sum(folder, cancel);
        }
        if (ExistingFile(executable) is not string program)
        {
            return null;
        }
        if (SpecificDirectory(Path.GetDirectoryName(program)) is string programFolder)
        {
            return Sum(programFolder, cancel);
        }
        return Length(program);
    }

    private static ulong? Sum(string root, CancellationToken cancel)
    {
        ulong total = 0;
        bool read = false;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            if (cancel.IsCancellationRequested)
            {
                break;
            }
            string directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string entry in entries)
            {
                if (cancel.IsCancellationRequested)
                {
                    return total > 0 ? total : null;
                }
                read = true;
                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                    else
                    {
                        total += Length(entry) ?? 0;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A file that cannot be read is left out of the total.
                }
            }
        }
        return read && total > 0 ? total : null;
    }

    private static ulong? Length(string file)
    {
        try
        {
            return (ulong)new FileInfo(file).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            string full = Path.GetFullPath(path.Trim().Trim('"'));
            return File.Exists(full) ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? SpecificDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!Directory.Exists(full) || full.Length <= 3)
        {
            return null;
        }
        foreach (Environment.SpecialFolder special in new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData,
        })
        {
            string root = Environment.GetFolderPath(special);
            if (root.Length > 0 && full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        string? downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (full.Equals(downloads, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return full;
    }
}
