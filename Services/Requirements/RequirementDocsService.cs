using ICOGenerator.Localization;
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

        Report("setup", Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync"));

        var project = await _db.Projects
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project not found: {projectId}.");

        var ba = await _agentResolver.GetRequiredAsync(cancellationToken);
        var model = ba.AiModel!;

        var productBrief = ProjectDocumentLookup.GetContent(project, _artifactCatalog.ProductBrief.FileName, versionName);
        if (string.IsNullOrWhiteSpace(productBrief))
            throw new InvalidOperationException(Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.2", versionName));

        // Spec là đầu vào duy nhất của bước dựng POC — đưa bối cảnh tổ chức (phòng ban/chức danh thật)
        // vào đây để dữ liệu mẫu của POC mang tên thật của đơn vị yêu cầu. Fail-open: không có dữ liệu
        // HR thì khối này rỗng, prompt như cũ.
        var organizationContext = await _orgContext.BuildCombinedContextAsync(project.OrgUnitCode, cancellationToken);

        // Dữ liệu mẫu THẬT từ file người dùng đính kèm (Excel/CSV/Word) — để POC dựng từ spec demo bằng
        // đúng danh mục của họ. Chỉ đọc, fail-open: không có file nào thì khối này rỗng và prompt như cũ.
        var realSampleData = await BuildRealSampleDataAsync(projectId, cancellationToken);

        var prompt = _promptBuilder.BuildAiDesignSpec(
            project,
            productBrief,
            ProjectDocumentLookup.GetContent(project, _artifactCatalog.AiDesignSpec.FileName, versionName),
            organizationContext ?? string.Empty,
            realSampleData,
            // Bảng phân quyền người dùng đã chốt từng ô. Không có khối này thì phân quyền chỉ tồn tại dưới
            // dạng văn xuôi trong Product Brief, và POC dựng ra một bộ màn hình không phân biệt vai — thứ
            // mà người xem demo không có cách nào nghiệm thu. Fail-open: chưa chốt ⇒ khối rỗng, prompt như cũ.
            PermissionMatrixBuilder.RenderConfirmedBlock(project.PermissionMatrix),
            // Các bảng chốt còn lại của buổi phỏng vấn. Mỗi bảng vá đúng một mục mà spec vốn phải TỰ NGHĨ
            // RA: luồng → chuyển trạng thái ở "## 8. Business Rules", màn hình → "## 6. Screens To
            // Generate" (phạm vi có ranh giới thay vì đếm lại từ văn xuôi). Mô hình dữ liệu thì spec KHÔNG
            // có — đó là việc của bước đề xuất kiến trúc sau khi POC được duyệt, xem ai-design-spec.v1.md.
            // Fail-open như trên: chưa chốt ⇒ khối rỗng, prompt như cũ.
            FlowMapBuilder.RenderConfirmedBlock(project.FlowMap),
            // Bản thiết kế UX: màn hình theo pattern, hành động + vị trí, điều hướng theo vai, hành trình.
            UxBlueprintRenderer.RenderSpecBlock(project.UxBlueprint),
            // Bảng báo cáo — các màn hình báo cáo đã có tên trong phạm vi, nhưng chỉ khối này chở được
            // chúng lấy số từ đâu và gộp/lọc theo chiều nào.
            ReportMapBuilder.RenderConfirmedBlock(project.ReportMap));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/ai-design-spec.v1.md")),
            new(ChatRole.User, prompt)
        };

        Report("tool", Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.3"));

        var (callResult, structuredSpec) = await _llm.ChatStructuredAsync<BAAiDesignSpecResult>(
            model, messages, new ModelCallLogContext(projectId, ba, "BAAiDesignSpec", workflowRunId), onToken, cancellationToken);

        // On a failed call, do NOT fall through to a fabricated spec: surface the failure so the caller can
        // report it and the user retries, rather than silently feeding an empty spec into the POC step.
        if (!callResult.IsSuccess)
        {
            var detail = callResult.ErrorMessage ?? callResult.Content;
            Report("error", Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.4"), detail);
            throw new InvalidOperationException($"LLM call failed: {detail}");
        }

        Report("observation", Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.5"));

        var result = structuredSpec != null
            ? _responseParser.Normalize(structuredSpec)
            : _responseParser.ParseAiDesignSpec(callResult.Content, productBrief);

        // Đối chiếu deterministic Brief ↔ Spec (màn hình, quy tắc nghiệp vụ): thứ gì của
        // Brief rơi rụng khỏi spec thì cho BA sửa lại ĐÚNG MỘT vòng (kèm báo cáo lệch), vì spec là đầu vào
        // duy nhất của POC — thiếu ở đây là POC thiếu luôn mà audit POC (chỉ so với spec) không thấy.
        // Fail-open: vòng sửa lỗi/vẫn lệch thì dùng bản tốt nhất đang có, không chặn pipeline.
        var parityReport = JoinReports(
            SpecBriefParityChecker.Check(productBrief, result.AiDesignSpec.Content),
            SpecBriefParityChecker.CheckBlueprint(project.UxBlueprint, result.AiDesignSpec.Content));
        if (parityReport != null)
        {
            Report("tool", Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.6"), parityReport);

            var fixPrompt = prompt
                + "\n\n## BẢN AI DESIGN SPEC VỪA SINH (chưa đạt — cần sửa)\n"
                + result.AiDesignSpec.Content
                + "\n\n## KẾT QUẢ ĐỐI CHIẾU TỰ ĐỘNG VỚI PRODUCT BRIEF\n"
                + parityReport
                + "\n\nHãy xuất lại TOÀN BỘ AI Design Spec: bổ sung ĐẦY ĐỦ mọi mục bị thiếu nêu trên theo đúng hướng dẫn của từng phần, giữ nguyên các phần đã đúng. Vẫn trả JSON đúng format cũ.";

            var fixMessages = new List<ChatMessage>
            {
                new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/ai-design-spec.v1.md")),
                new(ChatRole.User, fixPrompt)
            };

            var (fixCall, fixStructured) = await _llm.ChatStructuredAsync<BAAiDesignSpecResult>(
                model, fixMessages, new ModelCallLogContext(projectId, ba, "BAAiDesignSpecParityFix", workflowRunId), onToken, cancellationToken);

            if (fixCall.IsSuccess)
            {
                // Parse STRICT ở vòng sửa: hỏng thì giữ bản vòng đầu, KHÔNG rơi vào khung dự phòng.
                // Vòng này phải xuất lại toàn bộ spec nên là output dài nhất tuyến; model bị cắt giữa
                // chừng là chuyện thường, và lấy khung "Cần làm rõ" đè lên bản vòng đầu thì bước dựng POC
                // mất sạch đầu vào thật trong khi log vẫn chỉ nói "vẫn còn lệch" — xem TryParseAiDesignSpec.
                var fixedResult = fixStructured != null
                    ? _responseParser.Normalize(fixStructured)
                    : _responseParser.TryParseAiDesignSpec(fixCall.Content);

                if (fixedResult != null && !string.IsNullOrWhiteSpace(fixedResult.AiDesignSpec.Content))
                {
                    result = fixedResult;
                    var remaining = JoinReports(
                        SpecBriefParityChecker.Check(productBrief, result.AiDesignSpec.Content),
                        SpecBriefParityChecker.CheckBlueprint(project.UxBlueprint, result.AiDesignSpec.Content));
                    Report("observation", remaining == null
                        ? Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.10")
                        : Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.11"), remaining);
                }
                else
                {
                    Report("observation", Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.12"), fixCall.Content);
                }
            }
            else
            {
                Report("observation", Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.13"), fixCall.ErrorMessage ?? fixCall.Content);
            }
        }

        // Chèn TẤT ĐỊNH bản thiết kế UX: §5 điều hướng theo vai, §6c từng hành động + vị trí (audit POC đọc
        // mục này), §6d hành trình (kịch bản nghiệm thu đi theo). Xem UxSpecSections.
        result.AiDesignSpec.Content = UxSpecSections.Apply(result.AiDesignSpec.Content, project.UxBlueprint);

        Report("tool", Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.14"));

        await _documentGenerator.GenerateAiDesignSpecVersionFile(project, ba.Id, versionName, result);
        await _db.SaveChangesAsync(cancellationToken);

        Report("final", Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.15", versionName));
        return result.AiDesignSpec.Content;
    }

    private static string? JoinReports(params string?[] reports)
    {
        var kept = reports.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        return kept.Count == 0 ? null : string.Join("\n\n", kept);
    }

    // Dữ liệu mẫu THẬT cho spec — cùng hàm mà AuditPocContent dùng làm chuẩn đối chiếu, xem
    // RealSampleDataReader (hai đầu phải đọc cùng một nguồn, nếu không cổng kiểm sẽ chấm POC theo một
    // tập dữ liệu khác với tập đã đưa vào prompt).
    private Task<string?> BuildRealSampleDataAsync(Guid projectId, CancellationToken cancellationToken) =>
        RealSampleDataReader.ReadAsync(_db, projectId, cancellationToken);

    /// <summary>
    /// Lượt team dev trigger ở Agent Dashboard: sinh bộ tài liệu kỹ thuật (BRD/SRS/FSD/UserStories) từ
    /// Product Brief + AI Design Spec ĐÃ DUYỆT của phiên bản requirement mới nhất. Ghi thẳng vào phiên
    /// bản đó (đã duyệt) — không qua cổng draft như luồng phía user.
    /// </summary>
    public async Task GenerateTechnicalDocsAsync(Guid projectId, Action<string, string, string?>? onProgress = null, Action<string>? onToken = null, Guid? workflowRunId = null, string? revisionFeedback = null, CancellationToken cancellationToken = default)
    {
        void Report(string kind, string message, string? detail = null) => onProgress?.Invoke(kind, message, detail);

        Report("setup", Loc.T("RequirementDocsService.GenerateTechnicalDocsAsync"));

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
            .Where(x => x.VersionName.StartsWith("V"))
            .Select(x => x.VersionName)
            .OrderByDescending(v => int.TryParse(v.Replace("V", ""), out var n) ? n : 0)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(Loc.T("RequirementDocsService.GenerateTechnicalDocsAsync.2"));

        var productBrief = ProjectDocumentLookup.GetContent(project, _artifactCatalog.ProductBrief.FileName, latestVersion);
        var aiDesignSpec = ProjectDocumentLookup.GetContent(project, _artifactCatalog.AiDesignSpec.FileName, latestVersion);

        Report("thinking", Loc.T("RequirementDocsService.GenerateTechnicalDocsAsync.3"));

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

        Report("tool", Loc.T("RequirementDocsService.GenerateTechnicalDocsAsync.4"));

        var (callResult, structuredDraft) = await _llm.ChatStructuredAsync<BARequirementDocxResult>(
            model, messages, new ModelCallLogContext(projectId, ba, "BATechnicalDocs", workflowRunId), onToken, cancellationToken);

        if (!callResult.IsSuccess)
        {
            var detail = callResult.ErrorMessage ?? callResult.Content;
            Report("error", Loc.T("RequirementDocsService.GenerateTechnicalDocsAsync.5"), detail);
            throw new InvalidOperationException($"LLM call failed: {detail}");
        }

        Report("observation", Loc.T("RequirementDocsService.GenerateTechnicalDocsAsync.6"));

        var result = structuredDraft != null
            ? _responseParser.Normalize(structuredDraft)
            : _responseParser.Parse(callResult.Content, project, productBrief);

        Report("tool", Loc.T("RequirementDocsService.GenerateTechnicalDocsAsync.7"));

        // Ghi chú nguồn gốc cho lịch sử revision: phân biệt lần sinh thường với vòng "Yêu cầu chỉnh sửa"
        // (kèm chính nhận xét của người duyệt — nhìn lịch sử là biết bản này sửa vì lý do gì).
        var revisionChangeNote = string.IsNullOrWhiteSpace(revisionFeedback)
            ? Loc.T("RequirementDocsService.GenerateTechnicalDocsAsync.8", latestVersion)
            : Loc.T("RequirementDocsService.GenerateTechnicalDocsAsync.9", revisionFeedback);

        await _documentGenerator.GenerateTechnicalDocs(project, ba.Id, latestVersion, result, revisionChangeNote);
        await _db.SaveChangesAsync(cancellationToken);

        Report("final", Loc.T("RequirementDocsService.GenerateTechnicalDocsAsync.10", latestVersion), null);
    }
}
