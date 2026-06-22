using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows.Maf;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// Verifies the EF-backed MAF checkpoint store (the durability mechanism for the workflow engine):
// checkpoints round-trip, the index is scoped per session and filterable by parent.
public class EfWorkflowCheckpointStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public EfWorkflowCheckpointStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddSingleton<IApiKeyProtector, PassthroughApiKeyProtector>();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    private EfWorkflowCheckpointStore NewStore() =>
        new(_services.GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task CreateRetrieve_RoundTripsPayload()
    {
        var store = NewStore();
        var session = Guid.NewGuid().ToString();

        var info = await store.CreateCheckpointAsync(session, JsonSerializer.SerializeToElement(new { step = 42 }));
        var loaded = await store.RetrieveCheckpointAsync(session, info);

        Assert.Equal(42, loaded.GetProperty("step").GetInt32());
        Assert.Equal(session, info.SessionId);
    }

    [Fact]
    public async Task RetrieveIndex_IsScopedPerSession_AndFilterableByParent()
    {
        var store = NewStore();
        var session = Guid.NewGuid().ToString();

        var root = await store.CreateCheckpointAsync(session, JsonSerializer.SerializeToElement(new { step = 1 }));
        var child = await store.CreateCheckpointAsync(session, JsonSerializer.SerializeToElement(new { step = 2 }), root);

        // A checkpoint in an unrelated session must not leak into this one.
        await store.CreateCheckpointAsync(Guid.NewGuid().ToString(), JsonSerializer.SerializeToElement(new { step = 99 }));

        var all = (await store.RetrieveIndexAsync(session)).ToList();
        Assert.Equal(2, all.Count);

        var children = (await store.RetrieveIndexAsync(session, root)).ToList();
        Assert.Single(children);
        Assert.Equal(child.CheckpointId, children[0].CheckpointId);
    }

    [Fact]
    public async Task RetrieveCheckpoint_Throws_WhenMissing()
    {
        var store = NewStore();
        var missing = new CheckpointInfo(Guid.NewGuid().ToString(), "nope");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.RetrieveCheckpointAsync(missing.SessionId, missing));
    }

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
