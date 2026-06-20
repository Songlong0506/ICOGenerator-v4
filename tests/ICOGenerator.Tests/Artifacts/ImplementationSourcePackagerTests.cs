using System.IO.Compression;
using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class ImplementationSourcePackagerTests
{
    [Fact]
    public async Task CreateArchiveAsync_ZipsSourceFiles_WithRelativeEntryNames()
    {
        using var source = new TempDir();
        source.WriteFile("README.md", "# app");
        source.WriteFile(Path.Combine("src", "index.js"), "console.log(1);");

        var packager = new ImplementationSourcePackager();
        var zipPath = await packager.CreateArchiveAsync(source.Path);

        Assert.NotNull(zipPath);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath!);
            var entries = archive.Entries.Select(e => e.FullName).ToHashSet();

            // Entry names are relative to the source root and use forward slashes.
            Assert.Contains("README.md", entries);
            Assert.Contains("src/index.js", entries);
        }
        finally
        {
            File.Delete(zipPath!);
        }
    }

    [Fact]
    public async Task CreateArchiveAsync_SkipsRegenerableDirectories()
    {
        using var source = new TempDir();
        source.WriteFile("package.json", "{}");
        source.WriteFile(Path.Combine("node_modules", "left-pad", "index.js"), "// dep");
        source.WriteFile(Path.Combine("bin", "app.dll"), "binary");
        source.WriteFile(Path.Combine("obj", "build.cache"), "cache");
        source.WriteFile(Path.Combine(".git", "config"), "[core]");

        var packager = new ImplementationSourcePackager();
        var zipPath = await packager.CreateArchiveAsync(source.Path);

        Assert.NotNull(zipPath);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath!);
            var entries = archive.Entries.Select(e => e.FullName).ToList();

            Assert.Equal(new[] { "package.json" }, entries);
        }
        finally
        {
            File.Delete(zipPath!);
        }
    }

    [Fact]
    public async Task CreateArchiveAsync_ReturnsNull_WhenDirectoryMissing()
    {
        var packager = new ImplementationSourcePackager();
        var missing = Path.Combine(Path.GetTempPath(), $"icogen-missing-{Guid.NewGuid():N}");

        Assert.Null(await packager.CreateArchiveAsync(missing));
    }

    [Fact]
    public async Task CreateArchiveAsync_ReturnsNull_WhenOnlyExcludedFilesPresent()
    {
        using var source = new TempDir();
        source.WriteFile(Path.Combine("node_modules", "dep", "index.js"), "// dep");

        var packager = new ImplementationSourcePackager();

        Assert.Null(await packager.CreateArchiveAsync(source.Path));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"icogen-src-test-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void WriteFile(string relativePath, string content)
        {
            var full = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
