using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Bước "Write Requirement": sinh/cập nhật bản nháp Product Brief từ hội thoại BA — qua cổng readiness
/// TẤT ĐỊNH trên bản đồ bao phủ (trừ khi lượt cuối là lời mời đã qua chính cổng đó ngay trong chat, xem
/// <see cref="BAChatService"/>), soạn bằng LLM, một vòng tự soát/sửa, rồi ghi file .docx. Luồng chat nằm
/// ở <see cref="BAChatService"/>; các tài liệu sau Approve nằm ở <see cref="RequirementDocsService"/>.
/// </summary>
public class ProductBriefDraftService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly RequirementPromptBuilder _promptBuilder;
    private readonly RequirementResponseParser _responseParser;
    private readonly RequirementDocumentGenerator _documentGenerator;
    private readonly PromptTemplateService _promptTemplateService;
    private readonly SourceContextBuilder _sourceContextBuilder;
    private readonly IProjectArtifactCatalog _artifactCatalog;
    private readonly ChecklistGapMemoryService _checklistGapMemory;
    private readonly ProductBriefReviewParser _reviewParser;
    private readonly OrganizationContextService _orgContext;
    private readonly RequirementCoverageService _coverage;
    private readonly BAAgentResolver _agentResolver;
    private readonly BAConversationLog _conversationLog;

    public ProductBriefDraftService(
        AppDbContext db,
        ILlmClient llm,
        RequirementPromptBuilder promptBuilder,
        RequirementResponseParser responseParser,
        RequirementDocumentGenerator documentGenerator,
        PromptTemplateService promptTemplateService,
        SourceContextBuilder sourceContextBuilder,
        IProjectArtifactCatalog artifactCatalog,
        ChecklistGapMemoryService checklistGapMemory,
        ProductBriefReviewParser reviewParser,
        OrganizationContextService orgContext,
        RequirementCoverageService coverage,
        BAAgentResolver agentResolver,
        BAConversationLog conversationLog)
    {
        _db = db;
        _llm = llm;
        _promptBuilder = promptBuilder;
        _responseParser = responseParser;
        _documentGenerator = documentGenerator;
        _promptTemplateService = promptTemplateService;
        _sourceContextBuilder = sourceContextBuilder;
        _artifactCatalog = artifactCatalog;
        _checklistGapMemory = checklistGapMemory;
        _reviewParser = reviewParser;
        _orgContext = orgContext;
        _coverage = coverage;
        _agentResolver = agentResolver;
        _conversationLog = conversationLog;
    }

    /// <param name="onProgress">Callback (kind, message, detail) báo tiến độ live cho UI; có thể null khi gọi đồng bộ.</param>
    /// <param name="onToken">Callback nhận từng token nội dung khi model soạn tài liệu, để stream "đang gõ" lên UI.</param>
    /// <param name="workflowRunId">Run liên quan để gắn chi phí token vào đúng workflow run (null nếu gọi ngoài workflow).</param>
    public async Task<RequirementDraftOutcome> GenerateOrUpdateDraftAsync(Guid projectId, Action<string, string, string?>? onProgress = null, Action<string>? onToken = null, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        void Report(string kind, string message, string? detail = null) => onProgress?.Invoke(kind, message, detail);

        Report("setup", "Đang đọc hội thoại…");

        // AsSplitQuery: ba collection Include trên một query single-query JOIN chéo thành tích Descartes
        // |Documents| × |Conversations| × |SourceFiles| dòng, mỗi dòng lặp lại cả Content tài liệu lẫn text
        // hội thoại — tách mỗi collection một query. Vẫn tracked vì generator/ghi chú bên dưới ghi lên graph này.
        var project = await _db.Projects
            .Include(x => x.Documents)
            .Include(x => x.Conversations)
            .Include(x => x.SourceFiles)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project not found: {projectId}.");

        var ba = await _agentResolver.GetRequiredAsync(cancellationToken);
        var model = ba.AiModel!;

        // Transcript Hỏi–Đáp đầy đủ (BA hỏi / user trả lời) — KHÔNG chỉ lượt user, để câu trả lời ngắn
        // kiểu chip ("Nhân viên văn phòng") còn nguyên ngữ cảnh câu hỏi khi soạn tài liệu.
        var conversationTranscript = ConversationTranscriptBuilder.Build(project.Conversations);

        // Tài liệu nguồn (ảnh/PDF) của project → AIContent gắn kèm lượt soạn tài liệu (text PDF + ảnh nếu model vision).
        var sources = project.SourceFiles.OrderBy(s => s.CreatedAt).ToList();
        var sourceContents = _sourceContextBuilder.Build(sources, model.SupportsVision);

        // Cổng kiểm tra: tài liệu KHÔNG được phép chứa giả định, nên còn BẤT KỲ nhóm áp dụng nào chưa
        // [RÕ] trên bản đồ bao phủ thì hỏi lại NGAY (một lượt BA trong khung chat) và KHÔNG soạn tài
        // liệu — tránh sinh tài liệu rồi vứt đi/sinh lại (tốn token). Ready suy TẤT ĐỊNH từ bản đồ
        // (RequirementReadinessGate.Evaluate) — cùng nguồn chân lý với lời mời trong chat và panel tiến
        // độ. Trước khi xét phải gộp nốt các lượt chưa distill vào bản đồ: đường POC-feedback
        // (RoutePocFeedbackToRequirementUseCase) thêm lượt user rồi gọi thẳng vào đây, không đi qua lượt
        // chat nào để bản đồ kịp tươi. (Ghi chú ghim trên Brief KHÔNG qua đây — nó rẽ sang
        // ReviseDraftFromNotesAsync, vòng sửa có phạm vi.)
        //
        // NGOẠI LỆ: lượt cuối hội thoại là lời BA mời bấm "Write Requirement" ⇒ lời mời đó CHỈ tồn tại
        // sau khi chính cổng tất định này đã pass ngay trong bước chat (BAChatService) trên bản đồ hiện
        // hành, và chưa có gì mới kể từ đó — xét lại chỉ tốn một lượt distill vô ích. Bỏ qua gate ở
        // nhánh này; van "không giả định" của bước soạn tài liệu (needsClarification bên dưới) vẫn là
        // chốt chặn cuối nên chất lượng tài liệu không đổi.
        if (RequirementReadinessGate.IsVerifiedInviteLatestTurn(project.Conversations))
        {
            Report("thinking", "Yêu cầu đã được kiểm tra đủ ngay trong bước chat — bắt đầu soạn tài liệu.", conversationTranscript);
        }
        else
        {
            Report("thinking", "Đang kiểm tra mức độ đầy đủ của yêu cầu…", conversationTranscript);
            // Lượt gộp lỗi ⇒ bản đồ trả về là bản CŨ; cổng vẫn xét trên nó (fail-closed như cũ — thiếu
            // thông tin thì chặn, không bao giờ nới ra vì một lời gọi hỏng).
            var coverage = await _coverage.UpdateAndLoadAsync(project, ba, model, cancellationToken);
            // Hội thoại đi kèm để cổng không phát lại đúng câu chặn của lần bấm nút trước: người dùng bấm
            // "Write Requirement" hai lần mà chưa bổ sung gì là ca thường, và hai lượt giống hệt nhau đọc
            // lên như thể hệ thống không nhớ mình vừa hỏi gì.
            var readiness = RequirementReadinessGate.Evaluate(coverage.Map, project.Conversations);
            if (!readiness.Ready)
            {
                var question = string.IsNullOrWhiteSpace(readiness.Message)
                    ? "Mình cần làm rõ thêm vài thông tin trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                    : readiness.Message;
                // Không kèm chip: câu chặn của cổng là câu MỞ (xin mẩu thông tin còn thiếu), ô nhập của
                // khung chat là chỗ trả lời — xem RequirementReadiness.OpenEnded.
                await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", question, cancellationToken: cancellationToken);

                Report("final", "Cần bổ sung thông tin trước khi sinh tài liệu — xem câu hỏi trong khung chat.", question);
                return RequirementDraftOutcome.NeedsMoreInfo;
            }
        }

        Report("thinking", "Đang tổng hợp yêu cầu từ hội thoại…", conversationTranscript);

        // Bối cảnh tổ chức + đơn vị yêu cầu: để tài liệu dùng ĐÚNG tên phòng ban/HoD thật (mục phạm vi,
        // stakeholder) thay vì "TBD"/tên bịa. Cùng một khối này được đưa vào cả vòng tự soát/sửa bên dưới
        // để reviewer không coi các tên thật đó là chi tiết "tự thêm ngoài hội thoại".
        var organizationContext = await _orgContext.BuildCombinedContextAsync(project.OrgUnitCode, cancellationToken);

        // Chỉ mục của chính hội thoại (điều đã chốt / ví dụ đã xác nhận / điểm còn tồn đọng) — đi kèm cả
        // lượt soạn, lượt tự soát và lượt sửa. Xem RequirementPromptBuilder.DistilledStateSection.
        var distilledState = BuildDistilledState(project);

        var prompt = _promptBuilder.BuildProductBrief(
            project,
            conversationTranscript,
            ProjectDocumentLookup.GetContent(project, _artifactCatalog.ProductBrief.FileName, "draft"),
            organizationContext,
            distilledState);

        // Lượt user mang prompt soạn tài liệu + tài liệu nguồn (text/ảnh) đính kèm. Không có nguồn ⇒ chỉ một
        // TextContent, tương đương đường cũ.
        var userContents = new List<AIContent> { new TextContent(prompt) };
        userContents.AddRange(sourceContents.Contents);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/product-brief.v3.md")),
            new(ChatRole.User, userContents)
        };

        Report("tool", "Đang gọi AI để soạn bản mô tả sản phẩm (Product Brief)…");

        var (callResult, structuredDraft) = await _llm.ChatStructuredAsync<BAProductBriefResult>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAProductBrief", workflowRunId), onToken, cancellationToken);

        // On a failed call, do NOT fall through to the template fallback: it would fabricate documents from the raw user message and report success, hiding the failure. Fail the task instead.
        if (!callResult.IsSuccess)
        {
            var detail = callResult.ErrorMessage ?? callResult.Content;
            Report("error", "Lời gọi LLM thất bại.", detail);
            throw new InvalidOperationException($"LLM call failed: {detail}");
        }

        Report("observation", "AI đã trả về nội dung, đang phân tích kết quả…");

        // Structured output (when enabled) yields a typed result; otherwise parse the text. Both go through
        // the same normalization so downstream sees fully-populated sections.
        var result = structuredDraft != null
            ? _responseParser.Normalize(structuredDraft)
            : _responseParser.ParseProductBrief(callResult.Content, project, conversationTranscript);

        // Van thoát "không giả định" (lớp chốt chặn cuối sau cổng readiness — bắt phần bản đồ bao phủ
        // lỡ chấm [RÕ] non): model soạn tài liệu phát hiện còn điểm PHẢI tự giả định mới viết được thì
        // trả câu hỏi thay vì viết bừa. Xử lý y hệt đường cổng chặn: đẩy câu hỏi vào khung chat, KHÔNG sinh file.
        if (result.NeedsClarification)
        {
            var clarify = string.IsNullOrWhiteSpace(result.ClarifyingQuestion)
                ? "Mình cần làm rõ thêm một điểm trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                : result.ClarifyingQuestion;

            await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", clarify,
                result.ClarifyingSuggestions.Count > 0
                    ? JsonSerializer.Serialize(result.ClarifyingSuggestions)
                    : null,
                cancellationToken: cancellationToken);

            Report("final", "Cần bổ sung thông tin trước khi sinh tài liệu — xem câu hỏi trong khung chat.", clarify);
            return RequirementDraftOutcome.NeedsMoreInfo;
        }

        // Vòng TỰ SOÁT (đúng một vòng): reviewer đối chiếu bản nháp với hội thoại (bỏ sót/sai lệch/tự
        // thêm/giả định còn sót/thiếu mục) rồi sửa nếu có vấn đề. Fail-open toàn tuyến — soát/sửa lỗi thì dùng bản nháp đầu.
        result = await ReviewAndReviseDraftAsync(project, ba, model, conversationTranscript, organizationContext, distilledState, result, Report, onToken, workflowRunId, cancellationToken);

        Report("tool", "Đang tạo/cập nhật file tài liệu (.docx)…");

        await _documentGenerator.GenerateProductBriefDraftFiles(project, ba.Id, result);

        var assistantMessage = string.IsNullOrWhiteSpace(result.AssistantMessage)
            ? "Đã tạo/cập nhật bản mô tả sản phẩm (Product Brief) dễ hiểu cho bạn xem & duyệt."
            : result.AssistantMessage;

        // AppendAsync SaveChanges trên cùng DbContext scoped ⇒ flush luôn các thay đổi tài liệu mà
        // generator vừa ghi lên graph project, như đường cũ (một SaveChanges cho cả lượt).
        await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", assistantMessage, cancellationToken: cancellationToken);

        // Tài liệu đã sinh thành công ⇒ đây là lúc có bức tranh Q&A đầy đủ để rút "khoảng trống checklist"
        // (thông tin người dùng phải tự nêu ra mà BA chưa từng hỏi), gộp vào hồ sơ chung của Agent BA để
        // MỌI dự án MỚI sau này (của bất kỳ ai) được hỏi kỹ hơn. Chỉ chạy một lần/dự án; fail-open nếu lỗi.
        await _checklistGapMemory.HarvestAsync(project, ba, model, cancellationToken);

        Report("final", "Đã tạo/cập nhật tài liệu.", assistantMessage);
        return RequirementDraftOutcome.Generated;
    }

    /// <summary>
    /// Vòng SỬA CÓ PHẠM VI cho các ghi chú người dùng ghim thẳng lên bản xem trước Product Brief: giữ
    /// nguyên bản Brief hiện có, chỉ sửa các đoạn được chú. MỘT lời gọi LLM, KHÔNG cổng readiness, KHÔNG
    /// vòng tự soát, KHÔNG khối "Trạng thái đã chắt".
    ///
    /// Vì sao tách khỏi <see cref="GenerateOrUpdateDraftAsync"/>: đường cũ cho ghi chú đi qua đúng lượt
    /// soạn tài liệu, tức mỗi ghi chú một dòng lại kéo theo một lần VIẾT LẠI cả tài liệu từ transcript —
    /// cộng `temperature > 0` (đoạn không ai đụng vẫn bị diễn đạt lại), cộng vòng tự soát rà toàn tài liệu,
    /// cộng luật truy vết bắt bổ sung mọi điều đã chốt còn thiếu. Người dùng ghi chú một dòng rồi nhận về
    /// một bản Brief đổi hàng chục dòng, và không còn tin cái nút đó nữa.
    ///
    /// Cái giá đã cân nhắc: các yêu cầu bị rơi rụng từ lần soạn trước KHÔNG được kéo về ở lượt này —
    /// đó là việc của "Write Requirement" (nhắn một câu trong khung chat là cổng mở lại).
    ///
    /// Chưa có bản draft nào để sửa (ghi chú trên bản V{n} đã duyệt, hoặc file bị xóa) ⇒ rơi về đường
    /// soạn đầy đủ: không có bản gốc thì không có gì để giữ nguyên.
    /// </summary>
    public async Task<RequirementDraftOutcome> ReviseDraftFromNotesAsync(
        Guid projectId,
        IReadOnlyList<BriefNote> notes,
        Action<string, string, string?>? onProgress = null,
        Action<string>? onToken = null,
        Guid? workflowRunId = null,
        CancellationToken cancellationToken = default)
    {
        void Report(string kind, string message, string? detail = null) => onProgress?.Invoke(kind, message, detail);

        var clean = (notes ?? Array.Empty<BriefNote>())
            .Where(n => !string.IsNullOrWhiteSpace(n.Note))
            .ToList();

        if (clean.Count == 0)
            return await GenerateOrUpdateDraftAsync(projectId, onProgress, onToken, workflowRunId, cancellationToken);

        Report("setup", $"Đang áp {clean.Count} ghi chú lên bản mô tả sản phẩm…");

        // Không cần SourceFiles ở lượt này: bản gốc đã được soạn từ chúng rồi, đính lại ảnh/PDF chỉ tốn
        // token và mời model viết lại những đoạn không ai ghi chú.
        var project = await _db.Projects
            .Include(x => x.Documents)
            .Include(x => x.Conversations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project not found: {projectId}.");

        var currentBrief = ProjectDocumentLookup.GetContent(project, _artifactCatalog.ProductBrief.FileName, "draft");
        if (string.IsNullOrWhiteSpace(currentBrief))
        {
            Report("observation", "Chưa có bản nháp nào để sửa — soạn lại từ hội thoại.");
            return await GenerateOrUpdateDraftAsync(projectId, onProgress, onToken, workflowRunId, cancellationToken);
        }

        var ba = await _agentResolver.GetRequiredAsync(cancellationToken);
        var model = ba.AiModel!;

        var conversationTranscript = ConversationTranscriptBuilder.Build(project.Conversations);
        var organizationContext = await _orgContext.BuildCombinedContextAsync(project.OrgUnitCode, cancellationToken);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/product-brief-note-revision.v1.md")),
            new(ChatRole.User, _promptBuilder.BuildProductBriefNoteRevision(project, conversationTranscript, currentBrief, clean, organizationContext))
        };

        Report("tool", "Đang gọi AI để sửa đúng các đoạn được ghi chú…",
            string.Join("\n", clean.Select(n => "- " + n.Note.Trim())));

        var (callResult, structured) = await _llm.ChatStructuredAsync<BAProductBriefResult>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAProductBriefNoteRevision", workflowRunId), onToken, cancellationToken);

        if (!callResult.IsSuccess)
        {
            var detail = callResult.ErrorMessage ?? callResult.Content;
            Report("error", "Lời gọi LLM thất bại.", detail);
            throw new InvalidOperationException($"LLM call failed: {detail}");
        }

        // TryParse STRICT (không fallback template): bản sửa hỏng thì tài liệu đang có phải được giữ
        // NGUYÊN — ghi đè bản người dùng đang rà bằng một khung "Cần làm rõ" là mất trắng bản tốt.
        var revised = structured != null
            ? _responseParser.Normalize(structured)
            : _responseParser.TryParseProductBrief(callResult.Content);

        // Van thoát: ghi chú mâu thuẫn với điều đã chốt / không hiểu nổi ⇒ hỏi trong khung chat, KHÔNG
        // đụng vào tài liệu. Ghi chú của người dùng vẫn nằm trong transcript nên câu trả lời của họ ở
        // lượt sau vẫn có đủ ngữ cảnh.
        if (revised != null && revised.NeedsClarification)
        {
            var clarify = string.IsNullOrWhiteSpace(revised.ClarifyingQuestion)
                ? "Mình chưa rõ ý ghi chú của anh/chị. Anh/chị nói rõ hơn giúp nhé."
                : revised.ClarifyingQuestion;

            await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", clarify,
                revised.ClarifyingSuggestions.Count > 0
                    ? JsonSerializer.Serialize(revised.ClarifyingSuggestions)
                    : null,
                cancellationToken: cancellationToken);

            Report("final", "Cần làm rõ ghi chú trước khi sửa tài liệu — xem câu hỏi trong khung chat.", clarify);
            return RequirementDraftOutcome.NeedsMoreInfo;
        }

        // Bản sửa hỏng/rỗng: KHÔNG ghi gì cả và để task FAIL — im lặng ở đây là tệ nhất, người dùng sẽ
        // tưởng ghi chú đã được áp trong khi tài liệu y nguyên. Task failed thì UI có nút chạy lại.
        if (revised == null || string.IsNullOrWhiteSpace(revised.ProductBrief.Content))
        {
            Report("error", "Bản sửa không hợp lệ — giữ nguyên tài liệu đang có.", callResult.Content);
            throw new InvalidOperationException("Brief note revision returned no usable content.");
        }

        Report("tool", "Đang cập nhật file tài liệu (.docx)…");

        await _documentGenerator.GenerateProductBriefDraftFiles(project, ba.Id, revised,
            changeNote: $"Sửa Product Brief theo {clean.Count} ghi chú trên bản xem trước");

        var assistantMessage = string.IsNullOrWhiteSpace(revised.AssistantMessage)
            ? "Đã sửa bản mô tả sản phẩm theo các ghi chú của bạn."
            : revised.AssistantMessage;

        await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", assistantMessage, cancellationToken: cancellationToken);

        Report("final", "Đã sửa tài liệu theo ghi chú.", assistantMessage);
        return RequirementDraftOutcome.Generated;
    }

    // Vòng TỰ SOÁT bản nháp Product Brief + một vòng sửa duy nhất. Fail-open ở mọi nhánh: lời gọi soát
    // lỗi / parse hỏng ⇒ coi như đạt; lời gọi sửa lỗi / bản sửa rỗng ⇒ giữ bản nháp đầu. Vòng soát là
    // call nhẹ (chỉ trả danh sách vấn đề); vòng sửa chỉ chạy khi thật sự có vấn đề.
    private async Task<BAProductBriefResult> ReviewAndReviseDraftAsync(
        Project project,
        Agent ba,
        AiModel model,
        string conversationTranscript,
        string organizationContext,
        string distilledState,
        BAProductBriefResult draft,
        Action<string, string, string?> report,
        Action<string>? onToken,
        Guid? workflowRunId,
        CancellationToken cancellationToken)
    {
        // Không có nội dung để soát (model trả rỗng, hoặc fallback khung) thì soát cũng vô ích.
        if (string.IsNullOrWhiteSpace(draft.ProductBrief.Content))
            return draft;

        report("thinking", "Đang tự soát bản nháp so với hội thoại…", null);

        var reviewMessages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/product-brief-review.v2.md")),
            new(ChatRole.User, _promptBuilder.BuildProductBriefReview(project, conversationTranscript, draft.ProductBrief.Content, organizationContext, distilledState))
        };

        var (reviewCall, structuredReview) = await _llm.ChatStructuredAsync<ProductBriefReview>(
            model, reviewMessages, ba.Temperature, new ModelCallLogContext(project.Id, ba, "BAProductBriefReview", workflowRunId), cancellationToken: cancellationToken);

        if (!reviewCall.IsSuccess)
        {
            report("observation", "Tự soát không chạy được — dùng bản nháp hiện tại.", reviewCall.ErrorMessage);
            return draft;
        }

        var review = structuredReview != null
            ? _reviewParser.Normalize(structuredReview)
            : _reviewParser.Parse(reviewCall.Content);

        if (review.Issues.Count == 0)
        {
            report("observation", "Tự soát: bản nháp khớp hội thoại, không có vấn đề.", null);
            return draft;
        }

        report("tool", $"Tự soát phát hiện {review.Issues.Count} vấn đề — đang sửa bản nháp…",
            string.Join("\n", review.Issues.Select(i => "- " + i)));

        var revisionMessages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/product-brief.v3.md")),
            new(ChatRole.User, _promptBuilder.BuildProductBriefRevision(project, conversationTranscript, draft.ProductBrief.Content, review.Issues, organizationContext, distilledState))
        };

        var (revisionCall, structuredRevision) = await _llm.ChatStructuredAsync<BAProductBriefResult>(
            model, revisionMessages, ba.Temperature, new ModelCallLogContext(project.Id, ba, "BAProductBriefRevision", workflowRunId), onToken, cancellationToken);

        if (!revisionCall.IsSuccess)
        {
            report("observation", "Vòng sửa không chạy được — giữ bản nháp đầu.", revisionCall.ErrorMessage);
            return draft;
        }

        // Đường sửa dùng TryParse STRICT (không fallback template): bản sửa hỏng/rỗng thì giữ bản nháp
        // đầu — thà thiếu một vòng đánh bóng còn hơn ghi đè bản tốt bằng khung "Cần làm rõ".
        var revised = structuredRevision != null
            ? _responseParser.Normalize(structuredRevision)
            : _responseParser.TryParseProductBrief(revisionCall.Content);

        // Vòng sửa không được "trả bóng" needsClarification (prompt đã cấm): tới đây bản nháp đã tồn tại,
        // vấn đề dạng tự thêm/giả định phải sửa bằng cách xóa nội dung đó. Model vẫn cố trả cờ ⇒ coi như
        // bản sửa không hợp lệ, giữ bản nháp đầu.
        if (revised == null || revised.NeedsClarification || string.IsNullOrWhiteSpace(revised.ProductBrief.Content))
        {
            report("observation", "Bản sửa không hợp lệ — giữ bản nháp đầu.", null);
            return draft;
        }

        report("observation", "Đã sửa bản nháp theo kết quả tự soát.", null);
        return revised;
    }

    // Chỉ mục của chính hội thoại cho lượt soạn/soát/sửa Brief: các danh sách máy đã chắt sau mỗi lượt
    // chat (DecisionLogService, InterviewOutlookService). KHÔNG phải nguồn thông tin mới — mọi dòng ở đây
    // đều đã có trong transcript — nhưng là thứ biến "đừng bỏ sót yêu cầu nào" từ một lời dặn thành một
    // phép đối chiếu đếm được: mỗi mục phải tìm được chỗ tương ứng trong tài liệu.
    // Rỗng (dự án chưa chắt được gì) ⇒ chuỗi rỗng, prompt trở về đúng hình dạng cũ.
    private static string BuildDistilledState(Project project)
    {
        var sb = new StringBuilder();

        AppendBlock(sb, "Điều đã chốt (mỗi dòng là một quyết định của người dùng — tài liệu phải phản ánh hết)", project.DecisionLog);
        AppendBlock(sb, "Ví dụ đã xác nhận (input → kết quả kỳ vọng do người dùng chốt — quy tắc tương ứng phải có trong tài liệu)", project.WorkedExamples);
        // Danh sách tồn đọng KHÔNG chặn cổng readiness (cổng suy tất định từ bản đồ bao phủ). Ở đây nó có
        // tác dụng ngược lại và đúng chỗ: mục nào còn treo mà tài liệu buộc phải nói tới thì bước soạn
        // phải dùng van needsClarification, thay vì tự chọn một cách hiểu rồi viết ra như điều đã chốt.
        AppendBlock(sb, "Điểm cần làm rõ còn tồn đọng (CHƯA ai chốt — không được tự chọn một cách hiểu; cần tới mà chưa có thì dùng van needsClarification)", project.OpenQuestions);

        return sb.ToString().TrimEnd();
    }

    private static void AppendBlock(StringBuilder sb, string heading, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        if (sb.Length > 0)
            sb.AppendLine();
        sb.AppendLine($"### {heading}");
        sb.AppendLine(content.Trim());
    }
}
