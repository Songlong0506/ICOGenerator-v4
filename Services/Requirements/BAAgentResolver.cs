using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Tra cứu agent BA (RoleKey = BusinessAnalyst) kèm model — một chỗ duy nhất cho mọi use case requirement,
/// thay vì mỗi service tự query rồi tự xử lý thiếu cấu hình mỗi kiểu một chút.
/// </summary>
public class BAAgentResolver
{
    private readonly AppDbContext _db;

    public BAAgentResolver(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Bản "mềm" cho luồng chat: trả null khi chưa cấu hình BA agent HOẶC agent chưa gắn model. Thiếu
    /// cấu hình là chuyện vận hành, không phải crash bất thường — caller trả status để UI hiện thông báo
    /// thân thiện thay vì 500. Agent trả về luôn có <see cref="Agent.AiModel"/> khác null.
    /// </summary>
    public async Task<Agent?> FindConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken);
        return ba?.AiModel == null ? null : ba;
    }

    /// <summary>
    /// Bản "cứng" cho các bước sinh tài liệu (chạy trong workflow): thiếu cấu hình thì throw với thông
    /// điệp hướng dẫn để task fail rõ ràng, thay vì chạy tiếp với agent rỗng.
    /// </summary>
    public async Task<Agent> GetRequiredAsync(CancellationToken cancellationToken = default)
    {
        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken)
            ?? throw new InvalidOperationException(
                "Chưa cấu hình BA agent (RoleKey = BusinessAnalyst). Hãy tạo hoặc khôi phục agent BA trong màn hình Manage Agent.");

        if (ba.AiModel == null)
            throw new InvalidOperationException("BA agent model is not configured.");

        return ba;
    }
}
