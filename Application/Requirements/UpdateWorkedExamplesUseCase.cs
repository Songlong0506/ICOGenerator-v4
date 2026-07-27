using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum UpdateWorkedExamplesResult { Ok, ProjectNotFound, BaNotConfigured, TooMany }

/// <summary>
/// Sửa TAY danh sách "Ví dụ đã xác nhận" (<c>Project.WorkedExamples</c>) từ panel cạnh khung chat.
///
/// Vì sao đáng có một đường ghi riêng: đây là ORACLE ĐỘC LẬP chính xác nhất của cả hệ thống — spec đúc
/// các ví dụ này thành "## 13. Worked Examples" và POC phải TÍNH LẠI ra đúng con số/trạng thái đó
/// (<c>window.pocWorkedExamples</c>), nên một ví dụ chép sai từ hội thoại sẽ làm cả tầng tự kiểm chấm
/// theo chuẩn sai. Trước đây muốn sửa phải nói lại trong chat và chờ lượt chắt lọc đoán đúng ý.
///
/// Ghi ĐỒNG THỜI hai nơi là cố ý: cột trên Project (bước sinh spec đọc thẳng) và một lượt user trong hội
/// thoại — vì <see cref="InterviewOutlookService"/> chắt lại ba danh sách sau MỖI lượt chat từ "bản hiện
/// có + các lượt mới"; không có lượt hội thoại ghi nhận, bản sửa tay sẽ bị lượt chắt lọc sau đó lặng lẽ
/// viết đè về theo cách hiểu cũ.
/// </summary>
public class UpdateWorkedExamplesUseCase
{
    private const int MaxItems = 30;
    private const int MaxCharsPerItem = 500;

    private readonly AppDbContext _db;
    private readonly BAConversationLog _conversationLog;
    private readonly BAAgentResolver _agentResolver;

    public UpdateWorkedExamplesUseCase(
        AppDbContext db,
        BAConversationLog conversationLog,
        BAAgentResolver agentResolver)
    {
        _db = db;
        _conversationLog = conversationLog;
        _agentResolver = agentResolver;
    }

    public async Task<UpdateWorkedExamplesResult> ExecuteAsync(
        Guid projectId,
        IReadOnlyList<string> examples,
        CancellationToken cancellationToken = default)
    {
        var clean = (examples ?? Array.Empty<string>())
            .Select(e => (e ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim())
            .Where(e => e.Length > 0)
            .Select(e => e.Length > MaxCharsPerItem ? e[..MaxCharsPerItem] : e)
            .ToList();

        if (clean.Count > MaxItems)
            return UpdateWorkedExamplesResult.TooMany;

        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return UpdateWorkedExamplesResult.ProjectNotFound;

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return UpdateWorkedExamplesResult.BaNotConfigured;

        project.WorkedExamples = clean.Count == 0
            ? null
            : string.Join("\n", clean.Select(e => "- " + e));
        await _db.SaveChangesAsync(cancellationToken);

        var sb = new StringBuilder();
        if (clean.Count == 0)
        {
            sb.Append("Tôi đã xoá hết các ví dụ tính thử — hiện không có ví dụ nào cần dùng làm chuẩn kiểm.");
        }
        else
        {
            sb.AppendLine("Tôi chốt lại danh sách ví dụ tính thử (dùng đúng các ví dụ này làm chuẩn, đừng tự sửa):");
            foreach (var e in clean)
                sb.AppendLine("- " + e);
        }

        await _conversationLog.AppendAsync(projectId, ba.Id, "user", sb.ToString().TrimEnd(), cancellationToken: cancellationToken);
        return UpdateWorkedExamplesResult.Ok;
    }
}
