using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public class StartRequirementChatUseCase
{
    private readonly AppDbContext _db;

    public StartRequirementChatUseCase(AppDbContext db)
    {
        _db = db;
    }

    // Returns null when no BA agent is configured, so the (JSON) caller can report a
    // clean error instead of letting an exception bubble into the HTML error page.
    public async Task<Guid?> ExecuteAsync(Guid projectId, string message)
    {
        var ba = await _db.Agents.FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst);
        if (ba == null)
            return null;

        var job = new AgentJob
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            UserMessage = message,
            Status = AgentJobStatus.Queued,
            CurrentStep = "Queued..."
        };

        _db.AgentJobs.Add(job);
        await _db.SaveChangesAsync();
        return job.Id;
    }
}
