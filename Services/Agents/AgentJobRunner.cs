using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Agents;

public class AgentJobRunner
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AgentJobRunner(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void RunBARequirementJob(Guid jobId)
    {
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var baService = scope.ServiceProvider.GetRequiredService<BARequirementService>();

            var job = await db.AgentJobs.FirstAsync(x => x.Id == jobId);

            try
            {
                job.Status = "Running";
                job.CurrentStep = "BA is thinking...";
                await db.SaveChangesAsync();

                await Task.Delay(500);

                job.CurrentStep = "BA is analyzing your requirement...";
                await db.SaveChangesAsync();

                await baService.GenerateOrUpdateDraftAsync(
                    job.ProjectId,
                    job.UserMessage);

                job.Status = "Completed";
                job.CurrentStep = "Requirement draft updated.";
                job.Result = "Done";
                job.FinishedAt = DateTime.UtcNow;

                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                job.Status = "Failed";
                job.CurrentStep = "Failed";
                job.Error = ex.Message;
                job.FinishedAt = DateTime.UtcNow;

                await db.SaveChangesAsync();
            }
        });
    }
}