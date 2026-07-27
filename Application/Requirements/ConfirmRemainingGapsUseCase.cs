using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum ConfirmGapsStatus { Ok, ProjectNotFound, BaNotConfigured, NoDecisions }

/// <summary>
/// Kết quả một lượt chốt nhanh: bản đồ bao phủ SAU khi gộp (để panel tiến độ vẽ lại tại chỗ) và cờ
/// <see cref="Ready"/> = cổng "Write Requirement" đã mở hay chưa.
/// </summary>
public sealed record ConfirmGapsOutcome(
    ConfirmGapsStatus Status,
    IReadOnlyList<CoverageMapItem> Coverage,
    bool Ready)
{
    public static ConfirmGapsOutcome Failed(ConfirmGapsStatus status) =>
        new(status, Array.Empty<CoverageMapItem>(), false);
}

/// <summary>
/// Bước 2 của cổng "chốt nhanh phần còn lại": ghi các phương án người dùng đã duyệt (giữ nguyên hoặc gõ
/// đè) vào hội thoại như LỜI CỦA CHÍNH HỌ, rồi gộp ngay vào bản đồ bao phủ và đóng lượt bằng một lượt BA.
///
/// <para>
/// Ba điểm cố ý:
/// <list type="bullet">
/// <item>Ghi vào hội thoại (không phải một cột riêng) vì hội thoại là nguồn sự thật của MỌI tầng chắt lọc
/// — bản đồ bao phủ, "Điều đã chốt", triển vọng phỏng vấn, checklist-gap memory đều ăn theo transcript.
/// Một đường ghi riêng sẽ bị lượt chắt lọc kế tiếp lặng lẽ viết đè.</item>
/// <item>Gộp bản đồ NGAY trong lượt này (thay vì đợi lượt chat sau) vì đây là hành động mà người dùng
/// mong thấy kết quả tức thì: thanh tiến độ đầy và nút "Write Requirement" sáng lên. Chờ tới lượt chat
/// kế tiếp thì cổng vừa bấm trông như không có tác dụng gì.</item>
/// <item>Lượt BA đóng lượt chỉ được phép MỜI bấm "Write Requirement" khi cổng tất định
/// (<see cref="RequirementReadinessGate"/>) pass trên bản đồ vừa gộp — cùng bất biến với đường chat:
/// một lời mời được LƯU luôn đồng nghĩa bản đồ đã đủ, nên nút và panel không thể vênh nhau.</item>
/// </list>
/// </para>
/// </summary>
public class ConfirmRemainingGapsUseCase
{
    private const int MaxDecisions = 20;
    private const int MaxCharsPerAnswer = 1000;

    private readonly AppDbContext _db;
    private readonly BAConversationLog _conversationLog;
    private readonly BAAgentResolver _agentResolver;
    private readonly RequirementCoverageService _coverage;

    public ConfirmRemainingGapsUseCase(
        AppDbContext db,
        BAConversationLog conversationLog,
        BAAgentResolver agentResolver,
        RequirementCoverageService coverage)
    {
        _db = db;
        _conversationLog = conversationLog;
        _agentResolver = agentResolver;
        _coverage = coverage;
    }

    public async Task<ConfirmGapsOutcome> ExecuteAsync(
        Guid projectId,
        IReadOnlyList<GapDecision> decisions,
        CancellationToken cancellationToken = default)
    {
        var clean = (decisions ?? Array.Empty<GapDecision>())
            .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Answer))
            .Take(MaxDecisions)
            .Select(d => new GapDecision
            {
                Group = Flatten(d.Group),
                Question = Flatten(d.Question),
                Answer = Truncate(Flatten(d.Answer), MaxCharsPerAnswer)
            })
            .Where(d => d.Answer.Length > 0)
            .ToList();

        if (clean.Count == 0)
            return ConfirmGapsOutcome.Failed(ConfirmGapsStatus.NoDecisions);

        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return ConfirmGapsOutcome.Failed(ConfirmGapsStatus.ProjectNotFound);

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return ConfirmGapsOutcome.Failed(ConfirmGapsStatus.BaNotConfigured);

        await _conversationLog.AppendAsync(projectId, ba.Id, "user", BuildUserTurn(clean), cancellationToken: cancellationToken);

        // Gộp ngay vào bản đồ. Fail-open như mọi lượt distill: lời gọi lỗi ⇒ giữ bản đồ cũ, con trỏ đứng
        // yên, lượt chat sau gộp bù — người dùng vẫn còn nguyên các câu vừa chốt trong hội thoại.
        var coverageMap = await _coverage.UpdateAndLoadAsync(project, ba, ba.AiModel!, cancellationToken);
        var readiness = RequirementReadinessGate.Evaluate(coverageMap);

        await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", BuildBaTurn(clean.Count, readiness),
            cancellationToken: cancellationToken);

        return new ConfirmGapsOutcome(
            ConfirmGapsStatus.Ok,
            CoverageMapParser.Parse(coverageMap).ToList(),
            readiness.Ready);
    }

    // Lượt user: viết như chính người dùng nói ra, một dòng một điểm — đúng dạng mà mọi tầng chắt lọc
    // (bản đồ bao phủ, decision log, triển vọng phỏng vấn) vốn đọc từ hội thoại.
    private static string BuildUserTurn(List<GapDecision> decisions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tôi chốt luôn các điểm còn lại như sau:");
        foreach (var d in decisions)
        {
            sb.Append("- ");
            if (d.Group.Length > 0)
                sb.Append(d.Group).Append(": ");
            sb.AppendLine(d.Answer);
        }
        return sb.ToString().TrimEnd();
    }

    // Lượt BA đóng lượt: đủ ⇒ mời bấm nút (đúng chuỗi "Write Requirement" mà cổng/UI nhận diện); chưa đủ
    // ⇒ dùng NGUYÊN câu hỏi dựng sẵn của gate, nêu đúng nhóm còn thiếu.
    private static string BuildBaTurn(int count, RequirementReadiness readiness)
    {
        if (readiness.Ready)
            return $"Đã ghi nhận {count} điểm anh/chị vừa chốt — bản đồ khai thác yêu cầu đã đủ. "
                   + "Nếu không còn gì bổ sung, anh/chị bấm nút \"Write Requirement\" để mình soạn bản mô tả sản phẩm nhé.";

        return $"Đã ghi nhận {count} điểm anh/chị vừa chốt. "
               + (string.IsNullOrWhiteSpace(readiness.Message)
                   ? "Mình cần làm rõ thêm vài thông tin nữa trước khi viết tài liệu."
                   : readiness.Message);
    }

    private static string Flatten(string? text) =>
        (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;
}
