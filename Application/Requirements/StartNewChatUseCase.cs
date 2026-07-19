using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

// Backs the "＋ New Chat" button: archives the project's BA conversation history so the next
// chat starts fresh. KHÔNG xóa cứng — hội thoại là nguồn gốc của mọi tài liệu đã sinh, cần giữ
// lại tra cứu được; mọi đường đọc lọc ArchivedAt == null nên với BA lịch sử coi như trống.
public class StartNewChatUseCase
{
    private readonly AppDbContext _db;

    public StartNewChatUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task ExecuteAsync(Guid projectId)
    {
        var archivedAt = DateTime.UtcNow;
        await _db.AgentConversations
            .Where(c => c.ProjectId == projectId && c.ArchivedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ArchivedAt, archivedAt));

        // Xoá lượt chat mà giữ nguyên bộ nhớ per-project thì chat "mới" vẫn nhớ chuyện cũ (summary/bản đồ
        // cũ được nạp lại) và các con trỏ đếm-lượt trỏ vượt quá bảng đã trống — tệ nhất là
        // SummarizedTurnCount > 0 làm Skip() nuốt luôn các lượt mới, BA không thấy tin nhắn nào. Reset
        // toàn bộ trạng thái bộ nhớ gắn với hội thoại của project (hồ sơ USER trên AppUser là bộ nhớ
        // xuyên dự án, không đụng).
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null)
            return;

        project.ConversationSummary = null;
        project.SummarizedTurnCount = 0;
        project.UserMemoryHarvestedTurnCount = 0;
        project.RequirementCoverageMap = null;
        project.CoverageHarvestedTurnCount = 0;
        project.DecisionLog = null;
        project.DecisionHarvestedTurnCount = 0;
        await _db.SaveChangesAsync();
    }
}
