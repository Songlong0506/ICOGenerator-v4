using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Bộ nhớ CẤP NGƯỜI DÙNG cho BA chat — thứ làm nên cảm giác "càng nói chuyện càng hiểu user". Khác với
/// <see cref="ConversationMemoryService"/> (nhớ ngữ cảnh TRONG một dự án), service này chắt lọc DẦN các sự
/// thật BỀN về chính người dùng (vai trò, lĩnh vực, tổ chức, văn phong/định dạng ưa dùng, thuật ngữ hay
/// dùng…) và gom vào <see cref="AppUser.UserMemory"/> — một hồ sơ dùng lại XUYÊN SUỐT mọi dự án của họ.
/// Mỗi cuộc chat sẽ nạp lại hồ sơ này vào prompt nên BA "đã biết người dùng là ai" ngay từ lượt đầu.
/// <para>
/// Việc chắt lọc được <b>gom theo lô</b> (chỉ gọi LLM khi đã đủ một nhúm lượt mới chưa chắt lọc) để không
/// đốt token ở mỗi lượt, và <b>fail-open</b>: lời gọi lỗi thì giữ nguyên hồ sơ cũ + không dời con trỏ.
/// </para>
/// </summary>
public class UserMemoryService
{
    // Chỉ gọi LLM chắt lọc khi đã có ÍT NHẤT chừng này lượt MỚI (chưa chắt lọc) trong dự án, để batch và
    // đỡ token. Trong lúc chờ đạt ngưỡng, hồ sơ user hiện hành vẫn được nạp bình thường.
    public const int HarvestBatchThreshold = 10;

    // Chặn trên độ dài hồ sơ user để bộ nhớ dài hạn không tự phình vô hạn qua nhiều lần gộp.
    private const int MaxMemoryChars = 4000;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public UserMemoryService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Cập nhật hồ sơ user nếu vừa đủ một lô lượt mới, rồi trả về hồ sơ hiện hành (để caller nạp vào prompt).
    /// Hồ sơ gắn theo NGƯỜI TẠO project (<see cref="Project.CreatedByUsername"/>); project không có chủ thì
    /// bỏ qua (trả null) vì không biết quy bộ nhớ về ai. <paramref name="project"/> phải là entity ĐANG ĐƯỢC
    /// TRACK — con trỏ harvest được ghi thẳng lên nó. Fail-open: lời gọi LLM lỗi thì GIỮ hồ sơ cũ và KHÔNG
    /// dời con trỏ, lần sau gặp ngưỡng sẽ thử lại.
    /// </summary>
    public async Task<string?> UpdateAndLoadAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(project.CreatedByUsername))
            return null;

        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Username == project.CreatedByUsername, cancellationToken);
        if (user == null)
            return null;

        var total = await _db.AgentConversations.CountAsync(c => c.ProjectId == project.Id, cancellationToken);
        var harvested = project.UserMemoryHarvestedTurnCount;

        if (total - harvested >= HarvestBatchThreshold)
        {
            // Thứ tự ổn định (CreatedAt rồi Id) để con trỏ harvest khớp đúng các lượt đã đưa vào hồ sơ.
            var toHarvest = await _db.AgentConversations
                .Where(c => c.ProjectId == project.Id)
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .Skip(harvested)
                .ToListAsync(cancellationToken);

            var updated = await DistillAsync(user.UserMemory, toHarvest, ba, model, project.Id, cancellationToken);
            if (updated != null)
            {
                user.UserMemory = updated;
                project.UserMemoryHarvestedTurnCount = harvested + toHarvest.Count;
                await _db.SaveChangesAsync(cancellationToken);
            }
            // updated == null ⇒ chắt lọc lỗi: fail-open, giữ hồ sơ cũ + con trỏ cũ, nạp lại như dưới.
        }

        return user.UserMemory;
    }

    // Gộp hồ sơ hiện có + các lượt mới thành MỘT hồ sơ user duy nhất. Trả về null khi lời gọi lỗi/rỗng để
    // caller fail-open (giữ hồ sơ cũ, không dời con trỏ).
    private async Task<string?> DistillAsync(string? existingMemory, List<AgentConversation> turns, Agent ba, AiModel model, Guid projectId, CancellationToken cancellationToken)
    {
        if (turns.Count == 0)
            return existingMemory;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingMemory))
        {
            sb.AppendLine("## Hồ sơ người dùng hiện có (gộp/cập nhật cùng các lượt mới bên dưới)");
            sb.AppendLine(existingMemory.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("## Các lượt hội thoại mới cần chắt lọc vào hồ sơ");
        foreach (var t in turns)
        {
            var who = t.Role == "assistant" ? "BA" : "Người dùng";
            sb.AppendLine($"- {who}: {(t.Message ?? string.Empty).Trim()}");
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/user-memory.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var result = await _llm.ChatWithLogAsync(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAUserMemory"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Content))
            return null;

        var memory = result.Content.Trim();
        return memory.Length > MaxMemoryChars ? memory[..MaxMemoryChars] : memory;
    }
}
