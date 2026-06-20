using System.IO.Compression;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Packages a generated source directory (04_Implementation/src) into a single .zip for download.
/// Regenerable directories (node_modules, build output, VCS metadata) are skipped — the source's
/// README documents how to restore them, and bundling them would only bloat the archive.
/// </summary>
public class ImplementationSourcePackager
{
    // Matched per path segment, so nested copies (e.g. a sub-package's own node_modules) are skipped too.
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".vs"
    };

    /// <summary>
    /// Zips every packageable file under <paramref name="sourcePath"/> into a temp .zip and returns
    /// its path, or null when the directory is missing or contains no packageable files. The caller
    /// owns the returned file (stream it with <see cref="FileOptions.DeleteOnClose"/>, or delete it).
    /// </summary>
    public async Task<string?> CreateArchiveAsync(string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            return null;

        var files = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
            .Where(file => !IsInExcludedDirectory(sourcePath, file))
            .ToList();

        if (files.Count == 0)
            return null;

        var zipPath = Path.Combine(Path.GetTempPath(), $"icogen-src-{Guid.NewGuid():N}.zip");
        try
        {
            // Finish writing the archive inside this block so it is fully flushed before we return;
            // any IO failure surfaces here rather than mid-stream once the response is underway.
            await using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    var entryName = Path.GetRelativePath(sourcePath, file)
                        .Replace(Path.DirectorySeparatorChar, '/');

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(file);

                    // FileShare.ReadWrite so a file an agent still has open doesn't abort the whole zip.
                    await using var entryStream = entry.Open();
                    await using var fileStream = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await fileStream.CopyToAsync(entryStream);
                }
            }
        }
        catch
        {
            TryDelete(zipPath);
            throw;
        }

        return zipPath;
    }

    internal static bool IsInExcludedDirectory(string sourceRoot, string filePath)
    {
        var relative = Path.GetRelativePath(sourceRoot, filePath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Skip the last segment: it's the file name, not a directory.
        for (var i = 0; i < segments.Length - 1; i++)
            if (ExcludedDirectories.Contains(segments[i]))
                return true;

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort: a leftover temp file in the OS temp dir is harmless.
        }
    }
}
