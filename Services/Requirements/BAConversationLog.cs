using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Ghi một lượt hội thoại BA (user/assistant) vào <c>AgentConversations</c> kèm ước lượng token rồi
/// SaveChanges. Dùng chung AppDbContext scoped với caller nên mọi thay đổi đang tracked trên cùng scope
/// (tài liệu vừa sinh, ghi chú trên project…) được flush cùng lượt ghi này.
/// </summary>
public class BAConversationLog
{
    private readonly AppDbContext _db;

    public BAConversationLog(AppDbContext db)
    {
        _db = db;
    }

    public async Task AppendAsync(Guid projectId, Guid agentId, string role, string message, string? suggestionsJson = null, bool suggestionsMultiSelect = false, string? flowDiagramJson = null, string? attachmentsJson = null, CancellationToken cancellationToken = default)
    {
        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = agentId,
            Role = role,
            Message = message,
            Suggestions = suggestionsJson,
            SuggestionsMultiSelect = suggestionsMultiSelect,
            FlowDiagram = flowDiagramJson,
            Attachments = attachmentsJson,
            TokenUsed = TokenEstimator.Estimate(message)
        });
        await _db.SaveChangesAsync(cancellationToken);
    }
}
