using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Agents;

public class AgentJobRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentJobRunner> _logger;

    public AgentJobRunner(IServiceScopeFactory scopeFactory, ILogger<AgentJobRunner> logger)
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
                await ProcessNextQueuedJobAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while processing queued agent jobs.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task ProcessNextQueuedJobAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var baService = scope.ServiceProvider.GetRequiredService<BARequirementService>();

        var job = await db.AgentJobs
            .Where(x => x.Status == "Queued")
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (job == null)
            return;

        try
        {
            job.Status = "Running";
            job.CurrentStep = "BA is thinking...";
            await db.SaveChangesAsync(cancellationToken);

            job.CurrentStep = "BA is analyzing your requirement...";
            await db.SaveChangesAsync(cancellationToken);

            await baService.GenerateOrUpdateDraftAsync(job.ProjectId, job.UserMessage);

            job.Status = "Completed";
            job.CurrentStep = "Requirement draft updated.";
            job.Result = "Done";
            job.FinishedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            job.Status = "Failed";
            job.CurrentStep = "Failed";
            job.Error = ex.Message;
            job.FinishedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
