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
    // KHÔNG ghi cột AgentConversation.FlowDiagram nữa: sơ đồ luồng chỉ-đọc ở lượt mời đã gỡ (xem
    // docs/requirement-flow.md). Cột vẫn còn để các lượt CŨ đọc lại được trong bản xuất và transcript.

    private readonly AppDbContext _db;

    public BAConversationLog(AppDbContext db)
    {
        _db = db;
    }

    public async Task AppendAsync(Guid projectId, Guid agentId, string role, string message, string? suggestionsJson = null, bool suggestionsMultiSelect = false, string? attachmentsJson = null, string? questionsJson = null, string? columnMapJson = null, string? permissionMatrixJson = null, string? flowMapJson = null, string? screenScopeMapJson = null, string? entityMapJson = null, string? reportMapJson = null, string? notificationMapJson = null, CancellationToken cancellationToken = default)
    {
        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = agentId,
            Role = role,
            Message = message,
            Suggestions = suggestionsJson,
            SuggestionsMultiSelect = suggestionsMultiSelect,
            Questions = questionsJson,
            ColumnMap = columnMapJson,
            PermissionMatrix = permissionMatrixJson,
            FlowMap = flowMapJson,
            ScreenScopeMap = screenScopeMapJson,
            EntityMap = entityMapJson,
            ReportMap = reportMapJson,
            NotificationMap = notificationMapJson,
            Attachments = attachmentsJson,
            TokenUsed = TokenEstimator.Estimate(message)
        });
        await _db.SaveChangesAsync(cancellationToken);
    }
}
