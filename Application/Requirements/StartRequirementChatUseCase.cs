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

    public async Task<Guid> ExecuteAsync(Guid projectId, string message)
    {
        var ba = await _db.Agents.FirstAsync(x => x.Name == "BA");
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
