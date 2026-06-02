using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public record RequirementJobStatusDto(Guid Id, AgentJobStatus Status, string CurrentStep, string? Error);

public class GetRequirementJobStatusQuery
{
    private readonly AppDbContext _db;

    public GetRequirementJobStatusQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<RequirementJobStatusDto?> ExecuteAsync(Guid jobId)
    {
        return await _db.AgentJobs
            .AsNoTracking()
            .Where(x => x.Id == jobId)
            .Select(x => new RequirementJobStatusDto(x.Id, x.Status, x.CurrentStep, x.Error))
            .FirstOrDefaultAsync();
    }
}
