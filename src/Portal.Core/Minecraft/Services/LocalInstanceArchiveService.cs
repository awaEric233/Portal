using System.IO.Compression;

namespace Portal.Core.Minecraft.Services;

public static class LocalInstanceArchiveService
{
    private static readonly string[] InstanceDirectories =
    [
        "mods", "config", "resourcepacks", "shaderpacks", "saves", "scripts", "kubejs", "datapacks"
    ];

    private static readonly string[] InstanceFiles =
    [
        "options.txt", "servers.dat", "instance.cfg", "mmc-pack.json", "version.json"
    ];

    public static bool TryInspect(string archivePath, out string? suggestedInstanceId)
    {
        suggestedInstanceId = null;
        if (!File.Exists(archivePath)) return false;

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
            if (files.Length == 0) return false;

            var root = FindCommonRoot(files);
            suggestedInstanceId = root ?? Path.GetFileNameWithoutExtension(archivePath);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static async Task ExtractAsync(string archivePath, string destination, CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = PrepareEntries(archive);
        Directory.CreateDirectory(destination);

        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (entry, relativePath) = entries[index];
            var targetPath = Path.Combine(destination, relativePath);

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(targetPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await using var input = entry.Open();
                await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, useAsync: true);
                await input.CopyToAsync(output, cancellationToken);
            }

            progress?.Report((index + 1d) / entries.Count);
        }
    }

    private static List<(ZipArchiveEntry Entry, string RelativePath)> PrepareEntries(ZipArchive archive)
    {
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (files.Length == 0) throw new InvalidDataException("Archive contains no files.");

        var root = FindCommonRoot(files);
        var result = new List<(ZipArchiveEntry, string)>(archive.Entries.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            var relativePath = RemoveRoot(entry.FullName, root);
            if (string.IsNullOrEmpty(relativePath)) continue;
            ValidateRelativePath(relativePath);

            if (entry.Name.Length == 0)
            {
                if (paths.Contains(relativePath) || !directories.Add(relativePath))
                    throw new InvalidDataException("Archive contains conflicting entries.");
            }
            else if (!paths.Add(relativePath) || directories.Contains(relativePath))
            {
                throw new InvalidDataException("Archive contains conflicting entries.");
            }

            var parent = Path.GetDirectoryName(relativePath);
            while (!string.IsNullOrEmpty(parent))
            {
                if (paths.Contains(parent))
                    throw new InvalidDataException("Archive contains conflicting entries.");
                parent = Path.GetDirectoryName(parent);
            }

            result.Add((entry, relativePath));
        }

        return result;
    }

    private static string? FindCommonRoot(IReadOnlyCollection<ZipArchiveEntry> entries)
    {
        var firstSegments = entries.Select(entry => Normalize(entry.FullName).Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (firstSegments.Length != 1) return null;

        var root = firstSegments[0];
        if (InstanceDirectories.Contains(root, StringComparer.OrdinalIgnoreCase) ||
            InstanceFiles.Contains(root, StringComparer.OrdinalIgnoreCase)) return null;
        return entries.All(entry => Normalize(entry.FullName).StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            ? root
            : null;
    }

    private static string RemoveRoot(string path, string? root)
    {
        var normalized = Normalize(path);
        if (root is null) return normalized;
        return normalized.Length == root.Length ? string.Empty : normalized[(root.Length + 1)..];
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || Path.IsPathRooted(path) || Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Archive contains an invalid path.");
        if (path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Archive contains an invalid path.");
    }

}
