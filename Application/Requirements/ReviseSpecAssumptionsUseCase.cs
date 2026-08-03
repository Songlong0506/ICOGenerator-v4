using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
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
///
/// Phần CÒN LẠI của danh sách (những điểm user để nguyên "Đúng") được ghi vào
/// <c>Project.ConfirmedAssumptions</c>: cổng dựng lại ở lượt sinh mới, và không có trí nhớ này thì nó hỏi
/// lại nguyên văn cả những điểm vừa được duyệt — xem <see cref="AssumptionMemory"/>. Danh sách lấy từ
/// CHÍNH spec đang chờ (không phải từ payload client) để trí nhớ luôn khớp với thứ user thật sự nhìn thấy.
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
    private readonly IProjectArtifactCatalog _artifactCatalog;
    private readonly IWorkflowOrchestrator _workflowOrchestrator;

    public ReviseSpecAssumptionsUseCase(
        AppDbContext db,
        BAConversationLog conversationLog,
        BAAgentResolver agentResolver,
        IProjectArtifactCatalog artifactCatalog,
        IWorkflowOrchestrator workflowOrchestrator)
    {
        _db = db;
        _conversationLog = conversationLog;
        _agentResolver = agentResolver;
        _artifactCatalog = artifactCatalog;
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

        var project = await _db.Projects
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
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

        // Phần user KHÔNG bác trong danh sách đang chờ = phần họ đã duyệt đúng ⇒ nhớ lại để lượt sinh mới
        // không hỏi lại. Đồng thời QUÊN các điểm vừa bị bác: một giả định từng được duyệt ở vòng trước mà
        // nay user đổi ý thì phải rời trí nhớ, nếu không nó vĩnh viễn không được hỏi lại nữa.
        var rejected = clean.Select(c => c.Assumption).ToList();
        var rejectedKeys = rejected.Select(AssumptionMemory.Key).ToHashSet();
        var specContent = ProjectDocumentLookup.GetContent(project, _artifactCatalog.AiDesignSpec.FileName, version);
        var stillOk = SpecAssumptionsParser.Parse(specContent)
            .Where(a => !rejectedKeys.Contains(AssumptionMemory.Key(a)))
            .ToList();
        project.ConfirmedAssumptions = AssumptionMemory.Remember(project.ConfirmedAssumptions, stillOk, rejected);

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
