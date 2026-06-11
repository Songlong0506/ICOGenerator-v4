using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
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
            .Where(x => x.Status == AgentJobStatus.Queued)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (job == null)
            return;

        try
        {
            job.Status = AgentJobStatus.Running;
            job.CurrentStep = "BA is thinking...";
            await db.SaveChangesAsync(cancellationToken);

            await baService.ChatAsync(job.ProjectId, job.UserMessage);

            job.Status = AgentJobStatus.Completed;
            job.CurrentStep = "Done.";
            job.Result = "Done";
            job.FinishedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            job.Status = AgentJobStatus.Failed;
            job.CurrentStep = "Failed";
            job.Error = ex.Message;
            job.FinishedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
