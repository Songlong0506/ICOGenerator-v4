using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Các tài liệu sinh SAU khi requirement đã được duyệt: AI Design Spec (bước Approve) và bộ tài liệu kỹ
/// thuật BRD/SRS/FSD/UserStories (bước 2 của Delivery Pipeline). Cả hai ghi thẳng vào phiên bản V{n} đã
/// duyệt — không qua cổng readiness/draft như luồng phía user (xem <see cref="ProductBriefDraftService"/>).
/// </summary>
public class RequirementDocsService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly RequirementTemplateService _templateService;
    private readonly RequirementPromptBuilder _promptBuilder;
    private readonly RequirementResponseParser _responseParser;
    private readonly RequirementDocumentGenerator _documentGenerator;
    private readonly PromptTemplateService _promptTemplateService;
    private readonly IProjectArtifactCatalog _artifactCatalog;
    private readonly OrganizationContextService _orgContext;
    private readonly BAAgentResolver _agentResolver;

    public RequirementDocsService(
        AppDbContext db,
        ILlmClient llm,
        RequirementTemplateService templateService,
        RequirementPromptBuilder promptBuilder,
        RequirementResponseParser responseParser,
        RequirementDocumentGenerator documentGenerator,
        PromptTemplateService promptTemplateService,
        IProjectArtifactCatalog artifactCatalog,
        OrganizationContextService orgContext,
        BAAgentResolver agentResolver)
    {
        _db = db;
        _llm = llm;
        _templateService = templateService;
        _promptBuilder = promptBuilder;
        _responseParser = responseParser;
        _documentGenerator = documentGenerator;
        _promptTemplateService = promptTemplateService;
        _artifactCatalog = artifactCatalog;
        _orgContext = orgContext;
        _agentResolver = agentResolver;
    }

    /// <summary>
    /// Bước Approve (chạy đồng bộ): sinh AI Design Spec từ Product Brief ĐÃ DUYỆT của một phiên bản
    /// requirement (V{n}), ghi thẳng vào phiên bản đó (đã duyệt) và trả về nội dung spec để khởi động
    /// delivery workflow dựng POC. Tách khỏi "Write Requirement" để không sinh lại spec mỗi lần user
    /// chỉnh Product Brief — chỉ sinh một lần khi đã chốt requirement.
    /// </summary>
    /// <param name="onProgress">Callback (kind, message, detail) báo tiến độ live cho UI; null khi gọi đồng bộ.</param>
    /// <param name="onToken">Callback nhận từng token khi model soạn spec, để stream "đang gõ" lên UI.</param>
    /// <param name="workflowRunId">Run liên quan để gắn chi phí token vào đúng workflow run (null nếu gọi ngoài workflow).</param>
    public async Task<string> GenerateAiDesignSpecAsync(Guid projectId, string versionName, Action<string, string, string?>? onProgress = null, Action<string>? onToken = null, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        void Report(string kind, string message, string? detail = null) => onProgress?.Invoke(kind, message, detail);

        Report("setup", "Đang đọc Product Brief đã duyệt…");

        var project = await _db.Projects
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project not found: {projectId}.");

        var ba = await _agentResolver.GetRequiredAsync(cancellationToken);
        var model = ba.AiModel!;

        var productBrief = ProjectDocumentLookup.GetContent(project, _artifactCatalog.ProductBrief.FileName, versionName);
        if (string.IsNullOrWhiteSpace(productBrief))
            throw new InvalidOperationException($"Product Brief đã duyệt không tồn tại cho phiên bản {versionName}.");

        var prompt = _promptBuilder.BuildAiDesignSpec(
            project,
            productBrief,
            ProjectDocumentLookup.GetContent(project, _artifactCatalog.AiDesignSpec.FileName, versionName));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/ai-design-spec.v1.md")),
            new(ChatRole.User, prompt)
        };

        Report("tool", "Đang gọi AI để soạn AI Design Spec…");

        var (callResult, structuredSpec) = await _llm.ChatStructuredAsync<BAAiDesignSpecResult>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAAiDesignSpec", workflowRunId), onToken, cancellationToken);

        // On a failed call, do NOT fall through to a fabricated spec: surface the failure so the caller can
        // report it and the user retries, rather than silently feeding an empty spec into the POC step.
        if (!callResult.IsSuccess)
        {
            var detail = callResult.ErrorMessage ?? callResult.Content;
            Report("error", "Lời gọi LLM thất bại.", detail);
            throw new InvalidOperationException($"LLM call failed: {detail}");
        }

        Report("observation", "AI đã trả về nội dung, đang phân tích kết quả…");

        var result = structuredSpec != null
            ? _responseParser.Normalize(structuredSpec)
            : _responseParser.ParseAiDesignSpec(callResult.Content, productBrief);

        // Đối chiếu deterministic Brief ↔ Spec: màn hình nào của Brief bị rơi rụng khỏi "Screens To
        // Generate" thì cho BA sửa lại ĐÚNG MỘT vòng (kèm báo cáo lệch), vì spec là đầu vào duy nhất
        // của POC — thiếu ở đây là POC thiếu tính năng mà audit POC (chỉ so với spec) không thấy.
        // Fail-open: vòng sửa lỗi/vẫn lệch thì dùng bản tốt nhất đang có, không chặn pipeline.
        var parityReport = SpecBriefParityChecker.Check(productBrief, result.AiDesignSpec.Content);
        if (parityReport != null)
        {
            Report("tool", "Phát hiện màn hình rơi rụng so với Product Brief — đang yêu cầu BA bổ sung…", parityReport);

            var fixPrompt = prompt
                + "\n\n## BẢN AI DESIGN SPEC VỪA SINH (chưa đạt — cần sửa)\n"
                + result.AiDesignSpec.Content
                + "\n\n## KẾT QUẢ ĐỐI CHIẾU TỰ ĐỘNG VỚI PRODUCT BRIEF\n"
                + parityReport
                + "\n\nHãy xuất lại TOÀN BỘ AI Design Spec: BỔ SUNG heading `### 6.n. <Tên màn hình>` (kèm chi tiết) cho TỪNG màn hình bị thiếu nêu trên, giữ nguyên các phần đã đúng. Vẫn trả JSON đúng format cũ.";

            var fixMessages = new List<ChatMessage>
            {
                new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/ai-design-spec.v1.md")),
                new(ChatRole.User, fixPrompt)
            };

            var (fixCall, fixStructured) = await _llm.ChatStructuredAsync<BAAiDesignSpecResult>(
                model, fixMessages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAAiDesignSpecParityFix", workflowRunId), onToken, cancellationToken);

            if (fixCall.IsSuccess)
            {
                var fixedResult = fixStructured != null
                    ? _responseParser.Normalize(fixStructured)
                    : _responseParser.ParseAiDesignSpec(fixCall.Content, productBrief);

                if (!string.IsNullOrWhiteSpace(fixedResult.AiDesignSpec.Content))
                {
                    result = fixedResult;
                    var remaining = SpecBriefParityChecker.Check(productBrief, result.AiDesignSpec.Content);
                    Report("observation", remaining == null
                        ? "Spec đã bổ sung đủ các màn hình của Product Brief."
                        : "Spec sau vòng sửa vẫn còn lệch — tiếp tục với bản hiện có.", remaining);
                }
            }
            else
            {
                Report("observation", "Vòng sửa spec thất bại — tiếp tục với bản đã sinh.", fixCall.ErrorMessage ?? fixCall.Content);
            }
        }

        Report("tool", "Đang tạo/cập nhật file AI Design Spec (.docx)…");

        await _documentGenerator.GenerateAiDesignSpecVersionFile(project, ba.Id, versionName, result);
        await _db.SaveChangesAsync(cancellationToken);

        Report("final", $"Đã tạo AI Design Spec cho phiên bản {versionName}.");
        return result.AiDesignSpec.Content;
    }

    /// <summary>
    /// Lượt team dev trigger ở Agent Dashboard: sinh bộ tài liệu kỹ thuật (BRD/SRS/FSD/UserStories) từ
    /// Product Brief + AI Design Spec ĐÃ DUYỆT của phiên bản requirement mới nhất. Ghi thẳng vào phiên
    /// bản đó (đã duyệt) — không qua cổng draft như luồng phía user.
    /// </summary>
    public async Task GenerateTechnicalDocsAsync(Guid projectId, Action<string, string, string?>? onProgress = null, Action<string>? onToken = null, Guid? workflowRunId = null, string? revisionFeedback = null, CancellationToken cancellationToken = default)
    {
        void Report(string kind, string message, string? detail = null) => onProgress?.Invoke(kind, message, detail);

        Report("setup", "Đang đọc Product Brief & AI Design Spec đã duyệt và template tài liệu…");

        var brdTemplate = _templateService.GetBrdTemplate();
        var srsTemplate = _templateService.GetSrsTemplate();
        var fsdTemplate = _templateService.GetFsdTemplate();
        var userStoriesTemplate = _templateService.GetUserStoriesTemplate();

        var project = await _db.Projects
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project not found: {projectId}.");

        var ba = await _agentResolver.GetRequiredAsync(cancellationToken);
        var model = ba.AiModel!;

        // Phiên bản requirement đã duyệt mới nhất (V{n}). Không có ⇒ chưa Approve, không thể sinh tài liệu kỹ thuật.
        var latestVersion = project.Documents
            .Where(x => x.IsApproved && x.VersionName.StartsWith("V"))
            .Select(x => x.VersionName)
            .OrderByDescending(v => int.TryParse(v.Replace("V", ""), out var n) ? n : 0)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Chưa có phiên bản requirement nào được duyệt. Hãy Approve requirement trước khi tạo tài liệu kỹ thuật.");

        var productBrief = ProjectDocumentLookup.GetContent(project, _artifactCatalog.ProductBrief.FileName, latestVersion);
        var aiDesignSpec = ProjectDocumentLookup.GetContent(project, _artifactCatalog.AiDesignSpec.FileName, latestVersion);

        Report("thinking", "Đang soạn tài liệu kỹ thuật từ Product Brief & AI Design Spec đã duyệt…");

        // BRD/SRS có mục stakeholder/đơn vị liên quan: đưa bối cảnh tổ chức + đơn vị yêu cầu để các mục đó
        // mang tên phòng ban/HoD thật từ HR thay vì "TBD".
        var organizationContext = await _orgContext.BuildCombinedContextAsync(project.OrgUnitCode, cancellationToken);

        var prompt = _promptBuilder.BuildTechnicalDocs(
            project,
            productBrief,
            aiDesignSpec,
            ProjectDocumentLookup.GetContent(project, "BRD.docx", latestVersion),
            ProjectDocumentLookup.GetContent(project, "SRS.docx", latestVersion),
            ProjectDocumentLookup.GetContent(project, "FSD.docx", latestVersion),
            ProjectDocumentLookup.GetContent(project, "UserStories.docx", latestVersion),
            brdTemplate,
            srsTemplate,
            fsdTemplate,
            userStoriesTemplate,
            organizationContext,
            revisionFeedback);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/technical-docs.v1.md")),
            new(ChatRole.User, prompt)
        };

        Report("tool", "Đang gọi AI để soạn BRD, SRS, FSD, User Stories…");

        var (callResult, structuredDraft) = await _llm.ChatStructuredAsync<BARequirementDocxResult>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BATechnicalDocs", workflowRunId), onToken, cancellationToken);

        if (!callResult.IsSuccess)
        {
            var detail = callResult.ErrorMessage ?? callResult.Content;
            Report("error", "Lời gọi LLM thất bại.", detail);
            throw new InvalidOperationException($"LLM call failed: {detail}");
        }

        Report("observation", "AI đã trả về nội dung, đang phân tích kết quả…");

        var result = structuredDraft != null
            ? _responseParser.Normalize(structuredDraft)
            : _responseParser.Parse(callResult.Content, project, productBrief);

        Report("tool", "Đang tạo file tài liệu kỹ thuật (.docx)…");

        // Ghi chú nguồn gốc cho lịch sử revision: phân biệt lần sinh thường với vòng "Yêu cầu chỉnh sửa"
        // (kèm chính nhận xét của người duyệt — nhìn lịch sử là biết bản này sửa vì lý do gì).
        var revisionChangeNote = string.IsNullOrWhiteSpace(revisionFeedback)
            ? $"Sinh tài liệu kỹ thuật {latestVersion}"
            : $"Chỉnh sửa theo nhận xét: {revisionFeedback}";

        await _documentGenerator.GenerateTechnicalDocs(project, ba.Id, latestVersion, result, revisionChangeNote);
        await _db.SaveChangesAsync(cancellationToken);

        Report("final", $"Đã tạo tài liệu kỹ thuật (BRD, SRS, FSD, User Stories) cho phiên bản {latestVersion}.", null);
    }
}
