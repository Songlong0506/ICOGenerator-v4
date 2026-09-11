using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.WebPilot;

/// <summary>
/// Dự án "hộp cát" mà mọi lượt chạy WebPilot gắn vào.
///
/// <para>
/// Vì sao phải có: <see cref="Agents.AgentRunService.RunAsync"/> bắt buộc một <c>projectId</c> có
/// thật — nó dùng project để giải đường dẫn workspace và làm ngữ cảnh ghi log chi phí.
/// </para>
///
/// <para>
/// Vì sao KHÔNG cho người dùng chọn một dự án thật: mỗi lượt chạy agent được ghi vào
/// <c>AgentConversations</c> theo <c>ProjectId</c> — đúng bảng transcript mà phía yêu cầu đọc để dựng
/// ngữ cảnh BA (<see cref="Requirements.BAChatService"/> lọc theo project chứ không theo agent). Chạy
/// thử WebPilot trên một dự án thật sẽ chèn những lượt không liên quan vào hội thoại BA của dự án đó,
/// và chúng sẽ theo vào Product Brief ở vòng soạn kế tiếp.
/// </para>
/// </summary>
public class WebPilotSandboxProvider
{
    /// <summary>Tên cố định — cũng là cách nhận ra dự án này trong danh sách Projects.</summary>
    public const string ProjectName = "WebPilot Sandbox";

    private readonly AppDbContext _db;

    public WebPilotSandboxProvider(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Trả về id dự án hộp cát, tạo mới ở lần gọi đầu tiên.</summary>
    public async Task<Guid> GetOrCreateProjectIdAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _db.Projects
            .Where(x => x.Name == ProjectName)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is { } id)
            return id;

        var project = new Project
        {
            Name = ProjectName,
            Description = "Dự án hộp cát cho màn hình thử nghiệm WebPilot — không thuộc luồng yêu cầu hay delivery nào.",
            // Không dựng skeleton Bosch cho một dự án không bao giờ sinh code.
            IsUseBoschTemplate = false
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync(cancellationToken);
        return project.Id;
    }
}
