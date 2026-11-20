using ICOGenerator.Application.SystemLogs;
using ICOGenerator.Configuration;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Logging;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Context;
using Xunit;

namespace ICOGenerator.Tests.Logging;

// Đường log → bảng SystemLogs: sink lọc mức + chặn vòng lặp, writer ghi theo lô + dọn log cũ, query lọc.
// Chạy trên AppDbContext thật (Sqlite in-memory) như PermissionServiceTests.
public class DatabaseLogTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public DatabaseLogTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _services = new ServiceCollection()
            .AddSingleton<IApiKeyProtector, PassthroughApiKeyProtector>()
            .AddDbContext<AppDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private static DatabaseLogSink NewSink(SystemLogLevel min = SystemLogLevel.Warning)
        => new(Options.Create(new DatabaseLogSettings { MinimumLevel = min }));

    private static Serilog.ILogger LoggerFor(DatabaseLogSink sink)
        => new LoggerConfiguration().MinimumLevel.Verbose().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();

    private static List<SystemLog> Drain(DatabaseLogSink sink)
    {
        var list = new List<SystemLog>();
        while (sink.Reader.TryRead(out var x)) list.Add(x);
        return list;
    }

    private DatabaseLogWriter NewWriter(DatabaseLogSink sink, int retentionDays = 30, TimeProvider? time = null)
        => new(sink, _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DatabaseLogSettings { RetentionDays = retentionDays }),
            NullLogger<DatabaseLogWriter>.Instance, time);

    private AppDbContext NewDb() => _services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    [Fact]
    public void Sink_KeepsOnlyEventsAtOrAboveMinimumLevel()
    {
        var sink = NewSink();
        var log = LoggerFor(sink);

        log.Information("info");
        log.Warning("warn");
        log.Error("error");
        log.Fatal("fatal");

        Assert.Equal(new[] { "warn", "error", "fatal" }, Drain(sink).Select(x => x.Message));
    }

    [Fact]
    public async Task Sink_IgnoresEventsRaisedInsideSuppress_IncludingAcrossAwait()
    {
        var sink = NewSink();
        var log = LoggerFor(sink);

        using (DatabaseLogSink.Suppress())
        {
            await Task.Yield();
            log.Error("EF Core lỗi khi đang ghi lô log");
        }
        log.Error("lỗi thường");

        Assert.Equal(new[] { "lỗi thường" }, Drain(sink).Select(x => x.Message));
    }

    [Fact]
    public void Sink_MapsSourceContextRequestPathExceptionAndProperties()
    {
        var sink = NewSink();
        var log = LoggerFor(sink).ForContext("SourceContext", "ICOGenerator.Services.Workflows.AgentTaskWorker");

        using (LogContext.PushProperty("RequestPath", "/Requirements/ChatStream"))
            log.Error(new InvalidOperationException("hỏng"), "Task {TaskId} thất bại", 42);

        var entry = Assert.Single(Drain(sink));
        Assert.Equal(SystemLogLevel.Error, entry.Level);
        Assert.Equal("ICOGenerator.Services.Workflows.AgentTaskWorker", entry.SourceContext);
        Assert.Equal("Task 42 thất bại", entry.Message);
        Assert.Equal("/Requirements/ChatStream", entry.RequestPath);
        Assert.Contains("InvalidOperationException: hỏng", entry.Exception);
        Assert.Contains("\"TaskId\":42", entry.Properties);
        Assert.DoesNotContain("SourceContext", entry.Properties);
    }

    [Fact]
    public void Sink_ClipsHugeMessages()
    {
        var sink = NewSink();
        LoggerFor(sink).Error("{Body}", new string('x', DatabaseLogSink.MaxMessageLength * 2));

        var entry = Assert.Single(Drain(sink));
        Assert.Equal(DatabaseLogSink.MaxMessageLength, entry.Message.Length);
    }

    [Fact]
    public async Task Writer_PersistsQueuedEventsInBatches()
    {
        var sink = NewSink();
        var log = LoggerFor(sink);
        for (var i = 0; i < DatabaseLogWriter.BatchSize + 5; i++)
            log.Warning("dòng {I}", i);

        await NewWriter(sink).RunOnceAsync(CancellationToken.None);

        await using var db = NewDb();
        Assert.Equal(DatabaseLogWriter.BatchSize + 5, await db.SystemLogs.CountAsync());
        Assert.False(sink.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Writer_DropsBatch_WhenDatabaseFails_WithoutRequeueing()
    {
        var sink = NewSink();
        LoggerFor(sink).Error("sẽ mất khỏi DB, còn trong file");
        await using (var db = NewDb())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE SystemLogs");

        await NewWriter(sink).RunOnceAsync(CancellationToken.None);

        Assert.False(sink.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Writer_PurgesRowsOlderThanRetention()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        await using (var db = NewDb())
        {
            db.SystemLogs.Add(new SystemLog { Timestamp = now.UtcDateTime.AddDays(-31), Level = SystemLogLevel.Error, Message = "cũ" });
            db.SystemLogs.Add(new SystemLog { Timestamp = now.UtcDateTime.AddDays(-1), Level = SystemLogLevel.Error, Message = "mới" });
            await db.SaveChangesAsync();
        }

        await NewWriter(NewSink(), retentionDays: 30, time: new FixedTime(now)).RunOnceAsync(CancellationToken.None);

        await using var check = NewDb();
        Assert.Equal(new[] { "mới" }, await check.SystemLogs.Select(x => x.Message).ToListAsync());
    }

    [Fact]
    public async Task Query_FiltersByMinimumLevelAndSearch_NewestFirst()
    {
        var t = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = NewDb())
        {
            db.SystemLogs.AddRange(
                new SystemLog { Timestamp = t, Level = SystemLogLevel.Warning, Message = "chậm", SourceContext = "A" },
                new SystemLog { Timestamp = t.AddMinutes(1), Level = SystemLogLevel.Error, Message = "timeout LLM", SourceContext = "B" },
                new SystemLog { Timestamp = t.AddMinutes(2), Level = SystemLogLevel.Fatal, Message = "sập", Exception = "SqlException: timeout" });
            await db.SaveChangesAsync();
        }

        var query = new GetSystemLogPageQuery(NewDb(), Options.Create(new DatabaseLogSettings()));

        var errors = await query.ExecuteAsync(SystemLogLevel.Error);
        Assert.Equal(new[] { "sập", "timeout LLM" }, errors.Items.Select(x => x.Message));

        var search = await query.ExecuteAsync(search: "timeout");
        Assert.Equal(2, search.TotalCount);

        var all = await query.ExecuteAsync();
        Assert.Equal(3, all.TotalCount);
        Assert.False(all.HasFilter);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
