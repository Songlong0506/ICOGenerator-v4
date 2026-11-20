using ICOGenerator.Configuration;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ICOGenerator.Services.Logging;

/// <summary>
/// Worker nền rút hàng đợi của <see cref="DatabaseLogSink"/> và ghi xuống bảng <c>SystemLogs</c> theo lô,
/// đồng thời xoá dòng cũ hơn <see cref="DatabaseLogSettings.RetentionDays"/>.
///
/// Ghi hỏng (DB mất kết nối, bảng chưa có...) thì BỎ lô đó chứ không thử lại: mọi dòng vẫn có trong file
/// log, còn giữ lại trong RAM chờ DB sống lại chỉ đổi một sự cố thành hai. Lỗi ghi được báo ra log một lần
/// cho mỗi đợt hỏng liên tiếp (trong <see cref="DatabaseLogSink.Suppress"/> nên không quay lại hàng đợi).
/// </summary>
public sealed class DatabaseLogWriter : BackgroundService
{
    internal const int BatchSize = 200;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(6);

    private readonly DatabaseLogSink _sink;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DatabaseLogSettings _settings;
    private readonly ILogger<DatabaseLogWriter> _logger;
    private readonly TimeProvider _time;

    private bool _failing;
    private DateTimeOffset _nextRetentionAt = DateTimeOffset.MinValue;

    public DatabaseLogWriter(
        DatabaseLogSink sink,
        IServiceScopeFactory scopeFactory,
        IOptions<DatabaseLogSettings> settings,
        ILogger<DatabaseLogWriter> logger,
        TimeProvider? time = null)
    {
        _sink = sink;
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(FlushInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Tắt app: ghi nốt phần còn trong hàng đợi (thường chính là lỗi khiến app bị dừng).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (await FlushBatchAsync(cts.Token) == BatchSize) { }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Một vòng: ghi hết hàng đợi theo lô, rồi dọn log cũ nếu tới hạn.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        while (await FlushBatchAsync(cancellationToken) == BatchSize) { }
        await PurgeExpiredIfDueAsync(cancellationToken);
    }

    /// <summary>Ghi tối đa <see cref="BatchSize"/> dòng; trả về số dòng đã rút khỏi hàng đợi.</summary>
    private async Task<int> FlushBatchAsync(CancellationToken cancellationToken)
    {
        var batch = new List<SystemLog>(BatchSize);
        while (batch.Count < BatchSize && _sink.Reader.TryRead(out var entry))
            batch.Add(entry);
        if (batch.Count == 0)
            return 0;

        using var _ = DatabaseLogSink.Suppress();
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SystemLogs.AddRange(batch);
            await db.SaveChangesAsync(cancellationToken);
            _failing = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_failing)
                _logger.LogWarning(ex, "Không ghi được {Count} dòng log vào bảng SystemLogs — bỏ lô này (file log vẫn đủ). Các lần hỏng tiếp theo sẽ không báo lại cho tới khi ghi được.", batch.Count);
            _failing = true;
        }
        return batch.Count;
    }

    private async Task PurgeExpiredIfDueAsync(CancellationToken cancellationToken)
    {
        if (_settings.RetentionDays <= 0)
            return;
        var now = _time.GetUtcNow();
        if (now < _nextRetentionAt)
            return;
        _nextRetentionAt = now + RetentionInterval;

        var cutoff = now.UtcDateTime.AddDays(-_settings.RetentionDays);
        using var _ = DatabaseLogSink.Suppress();
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SystemLogs.Where(x => x.Timestamp < cutoff).ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Không xoá được log cũ hơn {RetentionDays} ngày trong bảng SystemLogs.", _settings.RetentionDays);
        }
    }
}
