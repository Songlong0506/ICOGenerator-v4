using ICOGenerator.Localization;

namespace ICOGenerator.Services.Diagnostics;

/// <summary>
/// Chạy MỌI <see cref="IEnvironmentCheck"/> đã đăng ký, song song, mỗi nhóm trong một DI scope riêng (nhiều
/// nhóm đọc <c>AppDbContext</c> — dùng chung một context giữa các task song song là lỗi của EF). Kết quả giữ
/// đúng thứ tự đăng ký để bảng hiển thị ổn định giữa các lần bấm.
/// </summary>
public sealed class EnvironmentDiagnostics
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EnvironmentDiagnostics> _logger;

    public EnvironmentDiagnostics(IServiceScopeFactory scopeFactory, ILogger<EnvironmentDiagnostics> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(
        EnvironmentCheckContext context, CancellationToken cancellationToken = default)
    {
        int count;
        using (var probe = _scopeFactory.CreateScope())
            count = probe.ServiceProvider.GetServices<IEnvironmentCheck>().Count();

        var tasks = Enumerable.Range(0, count)
            .Select(index => RunOneAsync(index, context, cancellationToken))
            .ToList();

        var groups = await Task.WhenAll(tasks);
        return groups.SelectMany(x => x).ToList();
    }

    private async Task<IReadOnlyList<EnvironmentCheckResult>> RunOneAsync(
        int index, EnvironmentCheckContext context, CancellationToken cancellationToken)
    {
        // Nhường luồng ngay: phần đồng bộ ở đầu một check (dò file, khởi chạy process) không được chặn các
        // check còn lại khởi động.
        await Task.Yield();

        using var scope = _scopeFactory.CreateScope();
        var check = scope.ServiceProvider.GetServices<IEnvironmentCheck>().ElementAt(index);

        using var timeout = new CancellationTokenSource(check.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            var run = check.RunAsync(context, linked.Token);
            // WhenAny thay vì chỉ trông vào token: một check kẹt ở API đồng bộ (không nghe token) vẫn không được
            // giữ cả bảng chờ theo nó.
            if (await Task.WhenAny(run, Task.Delay(check.Timeout, cancellationToken)) != run)
            {
                _ = run.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                return [TimedOut(check)];
            }

            return await run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return [TimedOut(check)];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Environment check {Group} threw.", check.Group);
            return
            [
                new EnvironmentCheckResult(check.Group, check.Group, EnvironmentCheckStatus.Fail,
                    Loc.T("EnvCheck.Runner.Crashed"), null, ex.Message)
            ];
        }
    }

    private static EnvironmentCheckResult TimedOut(IEnvironmentCheck check) =>
        new(check.Group, check.Group, EnvironmentCheckStatus.Fail,
            Loc.T("EnvCheck.Runner.Timeout", (int)check.Timeout.TotalSeconds));
}
