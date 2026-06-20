using ICOGenerator.Services.Artifacts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class BoschTemplateSeederTests
{
    // No BoschTemplate:*RepoUrl configured → both skeletons are skipped and git is never invoked.
    [Fact]
    public async Task SeedAsync_SkipsBoth_WhenNoTemplateReposConfigured()
    {
        using var root = new TempDir();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AgentWorkspace:RootPath"] = root.Path
        });
        var seeder = new BoschTemplateSeeder(config, new WorkspacePathResolver(config));

        var summary = await seeder.SeedAsync("proj-key", CancellationToken.None);

        Assert.Contains("backend", summary);
        Assert.Contains("frontend", summary);
        Assert.Contains("bỏ qua", summary);
    }

    // A backend skeleton already on disk must be left untouched (so retries never clobber agent edits),
    // and the seeder returns the "already present" note without invoking git.
    [Fact]
    public async Task SeedAsync_SkipsBackend_WhenSkeletonAlreadyPresent()
    {
        using var root = new TempDir();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AgentWorkspace:RootPath"] = root.Path,
            ["BoschTemplate:BackendRepoUrl"] = "https://example.com/backend.git"
        });
        var resolver = new WorkspacePathResolver(config);

        var backendDir = Path.Combine(
            resolver.GetImplementationSourcePath("proj-key"), BoschTemplateSeeder.BackendFolderName);
        Directory.CreateDirectory(backendDir);
        File.WriteAllText(Path.Combine(backendDir, "marker.txt"), "x");

        var seeder = new BoschTemplateSeeder(config, resolver);
        var summary = await seeder.SeedAsync("proj-key", CancellationToken.None);

        Assert.Contains("backend: skeleton đã có sẵn", summary);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"icogen-bosch-test-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
