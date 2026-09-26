using System.IO.Compression;

namespace Portal.Core.Minecraft.Services;

public static class LocalInstanceArchiveService
{
    private static readonly string[] InstanceDirectories =
    [
        "mods", "config", "resourcepacks", "shaderpacks", "saves", "scripts", "kubejs", "datapacks", "versions"
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
        await ExtractEntriesAsync(entries, destination, cancellationToken, progress);
    }

    public static IReadOnlyList<string> GetVersionIds(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        var root = FindCommonRoot(files);
        var versionIds = files
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => RemoveRoot(entry.FullName, root).Split('/'))
            .Where(parts => parts.Length >= 3 && parts[0].Equals("versions", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (versionIds.Length > 0) return versionIds;

        var versionFile = files.Select(entry => RemoveRoot(entry.FullName, root))
            .Select(Path.GetFileName)
            .FirstOrDefault(name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                                    files.Any(entry => string.Equals(
                                        Path.GetFileNameWithoutExtension(entry.Name) + ".jar", name,
                                        StringComparison.OrdinalIgnoreCase)));
        return versionFile is null ? [] : [Path.GetFileNameWithoutExtension(versionFile)];
    }

    public static async Task ExtractVersionAsync(string archivePath, string destination, string versionId,
        CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var root = FindCommonRoot(archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray());
        var prefix = $"versions/{versionId}/";
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => (Entry: entry, Path: RemoveRoot(entry.FullName, root)))
            .Where(item => !item.Path.StartsWith("versions/", StringComparison.OrdinalIgnoreCase) ||
                           item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Entry, item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? item.Path[prefix.Length..]
                : item.Path))
            .ToList();
        ValidateEntries(entries);
        await ExtractEntriesAsync(entries, destination, cancellationToken, progress);
    }

    private static async Task ExtractEntriesAsync(IReadOnlyList<(ZipArchiveEntry Entry, string RelativePath)> entries,
        string destination, CancellationToken cancellationToken, IProgress<double>? progress)
    {
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
        var entries = archive.Entries.Select(entry => (Entry: entry, Path: RemoveRoot(entry.FullName, root)))
            .Where(item => !string.IsNullOrEmpty(item.Path)).ToList();
        ValidateEntries(entries);
        return entries;
    }

    private static void ValidateEntries(IReadOnlyList<(ZipArchiveEntry Entry, string RelativePath)> entries)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, relativePath) in entries)
        {
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
        }
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
