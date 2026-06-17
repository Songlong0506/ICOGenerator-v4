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
        var ba = await _db.Agents.FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst)
            ?? throw new InvalidOperationException(
                "Chưa cấu hình BA agent (RoleKey = BusinessAnalyst). Hãy tạo hoặc khôi phục agent BA trong màn hình Manage Agent.");
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
