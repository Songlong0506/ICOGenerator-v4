namespace ICOGenerator.Services.Evals;

/// <summary>
/// BackgroundService canh các <see cref="Domain.EvalSchedule"/> đến hạn (cùng mẫu poll như EvalRunWorker)
/// rồi giao cho <see cref="EvalScheduleDispatcher"/> tạo EvalRun Queued. Poll thưa (30s) vì độ phân giải
/// lịch chỉ tính bằng GIỜ; việc chạy eval thật vẫn do EvalRunWorker đảm nhiệm.
/// </summary>
public class EvalScheduleWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EvalScheduleWorker> _logger;

    public EvalScheduleWorker(IServiceScopeFactory scopeFactory, ILogger<EvalScheduleWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<EvalScheduleDispatcher>();
                await dispatcher.DispatchDueAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while dispatching due eval schedules.");
            }

            // Nuốt cancellation của shutdown tại đây (như EvalRunWorker) để host coi là dừng sạch.
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
