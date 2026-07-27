using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum ReviseAssumptionsResult { Ok, ProjectNotFound, NothingPending, NoNotes, BaNotConfigured }

/// <summary>
/// Nhánh "có giả định chưa đúng" của cổng xác nhận giả định: người dùng đánh dấu các giả định sai (kèm ý
/// đúng của họ) ⇒ ghi một lượt user vào hội thoại BA, tích lũy đính chính lên
/// <c>Project.SpecAssumptionCorrections</c>, rồi SINH LẠI AI Design Spec cho đúng phiên bản đó — cổng
/// dựng lại ở lượt sinh mới nên user rà tiếp cho tới khi ưng.
///
/// Đính chính đi qua HAI đường là cố ý: lượt hội thoại giữ nguyên nguồn sự thật (bản đồ bao phủ, decision
/// log, checklist-gap memory đều ăn theo transcript như mọi lượt khác), còn cột đính chính là đường TẤT
/// ĐỊNH nạp thẳng vào prompt sinh spec — spec sinh từ Product Brief chứ không đọc transcript, nên nếu chỉ
/// ghi vào hội thoại thì lượt sinh lại vẫn đẻ ra đúng giả định vừa bị bác.
/// </summary>
public class ReviseSpecAssumptionsUseCase
{
    // Trần số đính chính gom trong một lượt (như ReviseBriefFromNotesUseCase) — chặn payload rác.
    private const int MaxNotes = 30;
    // Trần độ dài cột tích lũy: đính chính là ngữ cảnh nhắc lại, không phải nhật ký đầy đủ (hội thoại
    // mới là nơi lưu trọn). Chạm trần thì giữ phần MỚI nhất — điều user vừa nói mới là điều đang đúng.
    private const int MaxCorrectionChars = 4000;

    private readonly AppDbContext _db;
    private readonly BAConversationLog _conversationLog;
    private readonly BAAgentResolver _agentResolver;
    private readonly IWorkflowOrchestrator _workflowOrchestrator;

    public ReviseSpecAssumptionsUseCase(
        AppDbContext db,
        BAConversationLog conversationLog,
        BAAgentResolver agentResolver,
        IWorkflowOrchestrator workflowOrchestrator)
    {
        _db = db;
        _conversationLog = conversationLog;
        _agentResolver = agentResolver;
        _workflowOrchestrator = workflowOrchestrator;
    }

    /// <param name="corrections">
    /// Mỗi phần tử là "giả định bị bác" + ý đúng người dùng gõ (có thể bỏ trống phần ý đúng — khi đó
    /// chỉ ghi nhận là giả định đó KHÔNG đúng).
    /// </param>
    public async Task<ReviseAssumptionsResult> ExecuteAsync(
        Guid projectId,
        IReadOnlyList<AssumptionCorrection> corrections,
        CancellationToken cancellationToken = default)
    {
        var clean = (corrections ?? Array.Empty<AssumptionCorrection>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Assumption))
            .Take(MaxNotes)
            .ToList();
        if (clean.Count == 0)
            return ReviseAssumptionsResult.NoNotes;

        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return ReviseAssumptionsResult.ProjectNotFound;

        var version = project.PendingAssumptionsVersion;
        if (string.IsNullOrWhiteSpace(version))
            return ReviseAssumptionsResult.NothingPending;

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return ReviseAssumptionsResult.BaNotConfigured;

        var block = BuildCorrectionBlock(clean);

        // Gom vào cột tích lũy (mới nhất xuống dưới) rồi cắt từ ĐẦU nếu quá dài.
        var merged = string.IsNullOrWhiteSpace(project.SpecAssumptionCorrections)
            ? block
            : project.SpecAssumptionCorrections.TrimEnd() + "\n" + block;
        project.SpecAssumptionCorrections = merged.Length > MaxCorrectionChars
            ? merged[^MaxCorrectionChars..]
            : merged;

        // Gỡ cổng trước khi enqueue (như nhánh xác nhận): tránh hai lượt sinh lại chồng nhau.
        project.PendingAssumptionsVersion = null;
        await _db.SaveChangesAsync(cancellationToken);

        var message = new StringBuilder()
            .AppendLine("Tôi đã xem các giả định của bản thiết kế và thấy những điểm sau CHƯA đúng:")
            .AppendLine(block)
            .Append("Hãy cập nhật lại bản thiết kế cho khớp với các ý này.")
            .ToString();

        await _conversationLog.AppendAsync(projectId, ba.Id, "user", message, cancellationToken: cancellationToken);
        await _workflowOrchestrator.StartAiDesignSpecWorkflowAsync(projectId, version);
        return ReviseAssumptionsResult.Ok;
    }

    private static string BuildCorrectionBlock(List<AssumptionCorrection> corrections)
    {
        var sb = new StringBuilder();
        foreach (var c in corrections)
        {
            var assumption = c.Assumption.Trim();
            var correction = c.Correction?.Trim();
            sb.AppendLine(string.IsNullOrWhiteSpace(correction)
                ? $"- KHÔNG đúng: “{assumption}”"
                : $"- KHÔNG đúng: “{assumption}” → đúng ra là: {correction}");
        }
        return sb.ToString().TrimEnd();
    }
}
