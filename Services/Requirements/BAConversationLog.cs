using ICOGenerator.Data;
using ICOGenerator.Domain;

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

    public async Task AppendAsync(Guid projectId, Guid agentId, string role, string message, string? suggestionsJson = null, bool suggestionsMultiSelect = false, string? attachmentsJson = null, string? questionsJson = null, string? columnMapJson = null, string? flowMapJson = null, bool readinessVerified = false, bool sourceRequested = false, bool offTopic = false, CancellationToken cancellationToken = default)
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
            FlowMap = flowMapJson,
            Attachments = attachmentsJson,
            // Mặc định false: chỉ đường ghi nào TỰ KHẲNG ĐỊNH cổng readiness đã pass ở lượt này mới bật
            // (xem AgentConversation.ReadinessVerified — fail-closed là chủ ý).
            ReadinessVerified = readinessVerified,
            // Mặc định false, fail-open: lượt không tự khẳng định "đây là lời xin file" thì cùng lắm BA
            // xin thêm một lần (xem AgentConversation.SourceRequested).
            SourceRequested = sourceRequested,
            // Mặc định false: chỉ lượt BA đáp một câu ngoài phạm vi mới bật (xem AgentConversation.OffTopic).
            OffTopic = offTopic
        });
        await _db.SaveChangesAsync(cancellationToken);
    }
}
