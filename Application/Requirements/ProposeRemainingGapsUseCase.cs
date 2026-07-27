using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum ProposeGapsStatus { Ok, ProjectNotFound, BaNotConfigured, NothingPending, ProposalFailed }

public sealed record ProposeGapsOutcome(ProposeGapsStatus Status, IReadOnlyList<GapProposal> Proposals)
{
    public static ProposeGapsOutcome Failed(ProposeGapsStatus status) => new(status, Array.Empty<GapProposal>());
}

/// <summary>
/// Bước 1 của cổng "chốt nhanh phần còn lại": BA soạn sẵn một phương án cho MỖI nhóm thông tin bản đồ
/// bao phủ còn để trống, để người dùng duyệt một lần thay vì trả lời từng câu qua nhiều lượt chat.
/// Không ghi gì vào hội thoại — chỉ khi người dùng chốt (<see cref="ConfirmRemainingGapsUseCase"/>) các
/// phương án mới trở thành yêu cầu.
/// </summary>
public class ProposeRemainingGapsUseCase
{
    private readonly AppDbContext _db;
    private readonly BAAgentResolver _agentResolver;
    private readonly GapProposalService _gapProposals;

    public ProposeRemainingGapsUseCase(AppDbContext db, BAAgentResolver agentResolver, GapProposalService gapProposals)
    {
        _db = db;
        _agentResolver = agentResolver;
        _gapProposals = gapProposals;
    }

    public async Task<ProposeGapsOutcome> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return ProposeGapsOutcome.Failed(ProposeGapsStatus.ProjectNotFound);

        var pending = CoverageMapParser.Parse(project.RequirementCoverageMap)
            .Any(x => x.Status is "CHƯA HỎI" or "MỘT PHẦN");
        if (!pending)
            return ProposeGapsOutcome.Failed(ProposeGapsStatus.NothingPending);

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return ProposeGapsOutcome.Failed(ProposeGapsStatus.BaNotConfigured);

        var proposals = await _gapProposals.ProposeAsync(project, ba, ba.AiModel!, cancellationToken);
        return proposals.Count == 0
            ? ProposeGapsOutcome.Failed(ProposeGapsStatus.ProposalFailed)
            : new ProposeGapsOutcome(ProposeGapsStatus.Ok, proposals);
    }
}
