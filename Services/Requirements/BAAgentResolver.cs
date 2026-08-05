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
    /// Các agent BA theo thứ tự ỔN ĐỊNH. "First" trên một truy vấn KHÔNG có ORDER BY là thứ tự do SQL
    /// Server tùy ý chọn theo kế hoạch thực thi — hoàn toàn hợp lệ nếu DB có đúng một agent BA, nhưng có
    /// hai (một seed sẵn, một tạo tay ở màn Manage Agent) thì mỗi lời gọi có thể rơi vào một agent khác,
    /// tức là một MODEL khác, tức là một ENDPOINT khác. Triệu chứng của nó là thứ khó lần nhất: cùng một
    /// màn hình, luồng này chạy còn luồng kia báo lỗi kết nối. Cố định thứ tự ở đây để mọi luồng
    /// requirement luôn nhìn thấy cùng một agent BA.
    /// </summary>
    private IQueryable<Agent> OrderedBaAgents() =>
        _db.Agents
            .Where(x => x.RoleKey == AgentRoleKey.BusinessAnalyst)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id);

    /// <summary>
    /// Bản "mềm" cho luồng chat: trả null khi chưa cấu hình BA agent HOẶC agent chưa gắn model. Thiếu
    /// cấu hình là chuyện vận hành, không phải crash bất thường — caller trả status để UI hiện thông báo
    /// thân thiện thay vì 500. Agent trả về luôn có <see cref="Agent.AiModel"/> khác null.
    /// </summary>
    public async Task<Agent?> FindConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var ba = await OrderedBaAgents()
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(cancellationToken);
        return ba?.AiModel == null ? null : ba;
    }

    /// <summary>
    /// Bản KHÔNG đòi model, entity được TRACK: cho các thao tác chỉ đụng dữ liệu của agent (sửa checklist
    /// học được ở trang quản trị) — chúng không gọi LLM nên một agent chưa gắn model vẫn phải sửa được.
    /// </summary>
    public Task<Agent?> FindTrackedAsync(CancellationToken cancellationToken = default) =>
        OrderedBaAgents().FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Bản "cứng" cho các bước sinh tài liệu (chạy trong workflow): thiếu cấu hình thì throw với thông
    /// điệp hướng dẫn để task fail rõ ràng, thay vì chạy tiếp với agent rỗng.
    /// </summary>
    public async Task<Agent> GetRequiredAsync(CancellationToken cancellationToken = default)
    {
        var ba = await OrderedBaAgents()
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "Chưa cấu hình BA agent (RoleKey = BusinessAnalyst). Hãy tạo hoặc khôi phục agent BA trong màn hình Manage Agent.");

        if (ba.AiModel == null)
            throw new InvalidOperationException("BA agent model is not configured.");

        return ba;
    }
}
