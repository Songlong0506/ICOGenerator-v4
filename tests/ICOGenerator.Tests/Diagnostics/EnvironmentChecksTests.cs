using Xunit;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Diagnostics;
using ICOGenerator.Services.Diagnostics.Checks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace ICOGenerator.Tests.Diagnostics;

public class EnvironmentChecksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ico-envcheck-").FullName;
    private static readonly EnvironmentCheckContext Context = new(true);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("git version 2.43.0.windows.1", "2.43.0")]
    [InlineData("git version 2.30.1", "2.30.1")]
    [InlineData("8.0.404 [C:\\Program Files\\dotnet\\sdk]", "8.0.404")]
    [InlineData("v20.11.1", "20.11.1")]
    public void ParseVersion_ReadsTheFirstVersionNumber(string output, string expected)
        => Assert.Equal(Version.Parse(expected), ToolchainCheck.ParseVersion(output));

    [Fact]
    public async Task FileSystem_MissingWorkspaceAndContentFolders_Fail()
    {
        var check = new FileSystemCheck(Config(new() { ["AgentWorkspace:RootPath"] = Path.Combine(_dir, "nope") }), Env());

        var results = await check.RunAsync(Context, CancellationToken.None);

        Assert.Equal(EnvironmentCheckStatus.Fail, results.Single(r => r.Name == "AgentWorkspace:RootPath").Status);
        Assert.Equal(EnvironmentCheckStatus.Fail, results.Single(r => r.Name == "Templates").Status);
        Assert.Equal(EnvironmentCheckStatus.Fail, results.Single(r => r.Name == "Prompts").Status);
    }

    [Fact]
    public async Task FileSystem_WritableWorkspaceAndPublishedFolders_PassAndLeaveNoProbeFile()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_dir, "ws")).FullName;
        foreach (var folder in new[] { "Templates", "Prompts" })
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, folder)).FullName, "a.txt"), "x");
        var check = new FileSystemCheck(Config(new() { ["AgentWorkspace:RootPath"] = workspace }), Env());

        var results = await check.RunAsync(Context, CancellationToken.None);

        Assert.All(results.Where(r => r.Name is "AgentWorkspace:RootPath" or "Logs" or "Templates" or "Prompts"),
            r => Assert.Equal(EnvironmentCheckStatus.Pass, r.Status));
        Assert.Empty(Directory.GetFiles(workspace));
    }

    [Fact]
    public async Task FileSystem_RelativeWorkspace_Fails()
    {
        var check = new FileSystemCheck(Config(new() { ["AgentWorkspace:RootPath"] = "workspaces" }), Env());

        var results = await check.RunAsync(Context, CancellationToken.None);

        Assert.Equal(EnvironmentCheckStatus.Fail, results.Single(r => r.Name == "AgentWorkspace:RootPath").Status);
    }

    [Fact]
    public async Task Secrets_KeyLeftInAppSettingsJson_Warns_ButEnvironmentOverride_Passes()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """{ "Encryption": { "ApiKeyKey": "committed" } }""");
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = NewDb(connection);

        var fromFile = new ConfigurationBuilder().SetBasePath(_dir).AddJsonFile("appsettings.json").Build();
        var fileResults = await new SecretsCheck(fromFile, db).RunAsync(Context, CancellationToken.None);
        Assert.Equal(EnvironmentCheckStatus.Warning, fileResults.Single(r => r.Name == "Encryption:ApiKeyKey").Status);

        var overridden = new ConfigurationBuilder().SetBasePath(_dir).AddJsonFile("appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:ApiKeyKey"] = "from-env" }).Build();
        var overrideResults = await new SecretsCheck(overridden, db).RunAsync(Context, CancellationToken.None);
        Assert.Equal(EnvironmentCheckStatus.Pass, overrideResults.Single(r => r.Name == "Encryption:ApiKeyKey").Status);
    }

    [Fact]
    public async Task Secrets_ActiveHttpModelWithoutReadableKey_Warns()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = NewDb(connection);
        db.AiModels.Add(new AiModel { ModelId = "no-key", Endpoint = "https://api.example.com/v1", ApiKey = "" });
        db.AiModels.Add(new AiModel { ModelId = "cli", Endpoint = "claude-code://subscription", ApiKey = "" });
        await db.SaveChangesAsync();

        var results = await new SecretsCheck(Config(new() { ["Encryption:ApiKeyKey"] = "k" }), db).RunAsync(Context, CancellationToken.None);

        var stored = results.Last();
        Assert.Equal(EnvironmentCheckStatus.Warning, stored.Status);
        Assert.Contains("no-key", stored.Message);
        Assert.DoesNotContain("cli", stored.Message);
    }

    private static AppDbContext NewDb(SqliteConnection connection)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, new PassthroughApiKeyProtector());
        db.Database.EnsureCreated();
        return db;
    }

    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private IWebHostEnvironment Env() => new FakeEnv { ContentRootPath = _dir };

    private sealed class FakeEnv : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "ICOGenerator";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Production";
    }
}
