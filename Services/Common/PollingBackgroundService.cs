namespace ICOGenerator.Services.Common;

public abstract class PollingBackgroundService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected abstract ILogger Logger { get; }
    protected abstract string LoopErrorMessage { get; }
    protected abstract Task ProcessNextAsync(CancellationToken cancellationToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, LoopErrorMessage);
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
