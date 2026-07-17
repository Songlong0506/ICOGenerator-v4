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
/// (trừ khi lượt cuối là lời mời đã được gate duyệt ngay trong chat, xem <see cref="BAChatService"/>),
/// soạn bằng LLM, một vòng tự soát/sửa, rồi ghi file .docx. Luồng chat nằm ở <see cref="BAChatService"/>;
/// các tài liệu sau Approve nằm ở <see cref="RequirementDocsService"/>.
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
    private readonly RequirementReadinessGate _readinessGate;
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
        RequirementReadinessGate readinessGate,
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
        _readinessGate = readinessGate;
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

        // Cổng kiểm tra: tài liệu KHÔNG được phép chứa giả định, nên còn BẤT KỲ điểm nào sẽ phải giả
        // định (kể cả điểm phụ) thì hỏi lại NGAY (một lượt BA trong khung chat) và KHÔNG soạn tài liệu —
        // tránh sinh tài liệu rồi vứt đi/sinh lại (tốn token). Đây là một lời gọi LLM nhẹ (chỉ trả câu
        // hỏi). Kèm tóm tắt text tài liệu nguồn để readiness tính cả tài liệu đính kèm, và bản đồ bao
        // phủ (nếu có) để gate đối chiếu từng nhóm thay vì đoán lại từ đầu.
        //
        // NGOẠI LỆ: lượt cuối hội thoại là lời BA mời bấm "Write Requirement" ⇒ lời mời đó CHỈ tồn tại
        // sau khi chính cổng này đã pass ngay trong bước chat (BAChatService.ChatAsync) trên đúng
        // transcript hiện tại, và chưa có gì mới kể từ đó. Chạy lại gate vừa tốn một lời gọi vừa có thể
        // "đổi ý" (LLM không tất định) — chính là vòng lặp khó chịu "mời bấm nút xong lại chặn cần bổ
        // sung thông tin". Bỏ qua gate ở nhánh này; van "không giả định" của bước soạn tài liệu
        // (needsClarification bên dưới) vẫn là chốt chặn cuối nên chất lượng tài liệu không đổi.
        if (RequirementReadinessGate.IsVerifiedInviteLatestTurn(project.Conversations))
        {
            Report("thinking", "Yêu cầu đã được kiểm tra đủ ngay trong bước chat — bắt đầu soạn tài liệu.", conversationTranscript);
        }
        else
        {
            Report("thinking", "Đang kiểm tra mức độ đầy đủ của yêu cầu…", conversationTranscript);
            var readiness = await _readinessGate.CheckAsync(projectId, ba, model,
                conversationTranscript
                    + RequirementReadinessGate.BuildSourceBriefNote(sources)
                    + RequirementReadinessGate.BuildCoverageNote(project),
                cancellationToken);
            if (!readiness.Ready)
            {
                var question = string.IsNullOrWhiteSpace(readiness.Message)
                    ? "Mình cần làm rõ thêm vài thông tin trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                    : readiness.Message;
                var pendingSuggestions = readiness.Suggestions.Count > 0
                    ? JsonSerializer.Serialize(readiness.Suggestions)
                    : null;

                await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", question, pendingSuggestions, cancellationToken);

                Report("final", "Cần bổ sung thông tin trước khi sinh tài liệu — xem câu hỏi trong khung chat.", question);
                return RequirementDraftOutcome.NeedsMoreInfo;
            }
        }

        Report("thinking", "Đang tổng hợp yêu cầu từ hội thoại…", conversationTranscript);

        // Bối cảnh tổ chức + đơn vị yêu cầu: để tài liệu dùng ĐÚNG tên phòng ban/HoD thật (mục phạm vi,
        // stakeholder) thay vì "TBD"/tên bịa. Cùng một khối này được đưa vào cả vòng tự soát/sửa bên dưới
        // để reviewer không coi các tên thật đó là chi tiết "tự thêm ngoài hội thoại".
        var organizationContext = await _orgContext.BuildCombinedContextAsync(project.OrgUnitCode, cancellationToken);

        var prompt = _promptBuilder.BuildProductBrief(
            project,
            conversationTranscript,
            ProjectDocumentLookup.GetContent(project, _artifactCatalog.ProductBrief.FileName, "draft"),
            organizationContext);

        // Lượt user mang prompt soạn tài liệu + tài liệu nguồn (text/ảnh) đính kèm. Không có nguồn ⇒ chỉ một
        // TextContent, tương đương đường cũ.
        var userContents = new List<AIContent> { new TextContent(prompt) };
        userContents.AddRange(sourceContents);
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

        // Van thoát "không giả định" (lớp chốt chặn sau cổng readiness — vốn fail-open khi lỗi): model
        // soạn tài liệu phát hiện còn điểm PHẢI tự giả định mới viết được thì trả câu hỏi thay vì viết
        // bừa. Xử lý y hệt đường cổng chặn: đẩy câu hỏi vào khung chat, KHÔNG sinh file.
        if (result.NeedsClarification)
        {
            var clarify = string.IsNullOrWhiteSpace(result.ClarifyingQuestion)
                ? "Mình cần làm rõ thêm một điểm trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                : result.ClarifyingQuestion;

            await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", clarify,
                result.ClarifyingSuggestions.Count > 0
                    ? JsonSerializer.Serialize(result.ClarifyingSuggestions)
                    : null,
                cancellationToken);

            Report("final", "Cần bổ sung thông tin trước khi sinh tài liệu — xem câu hỏi trong khung chat.", clarify);
            return RequirementDraftOutcome.NeedsMoreInfo;
        }

        // Vòng TỰ SOÁT (đúng một vòng): reviewer đối chiếu bản nháp với hội thoại (bỏ sót/sai lệch/tự
        // thêm/giả định còn sót/thiếu mục) rồi sửa nếu có vấn đề. Fail-open toàn tuyến — soát/sửa lỗi thì dùng bản nháp đầu.
        result = await ReviewAndReviseDraftAsync(project, ba, model, conversationTranscript, organizationContext, result, Report, onToken, workflowRunId, cancellationToken);

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

    // Vòng TỰ SOÁT bản nháp Product Brief + một vòng sửa duy nhất. Fail-open ở mọi nhánh: lời gọi soát
    // lỗi / parse hỏng ⇒ coi như đạt; lời gọi sửa lỗi / bản sửa rỗng ⇒ giữ bản nháp đầu. Vòng soát là
    // call nhẹ (chỉ trả danh sách vấn đề); vòng sửa chỉ chạy khi thật sự có vấn đề.
    private async Task<BAProductBriefResult> ReviewAndReviseDraftAsync(
        Project project,
        Agent ba,
        AiModel model,
        string conversationTranscript,
        string organizationContext,
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
            new(ChatRole.User, _promptBuilder.BuildProductBriefReview(project, conversationTranscript, draft.ProductBrief.Content, organizationContext))
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
            new(ChatRole.User, _promptBuilder.BuildProductBriefRevision(project, conversationTranscript, draft.ProductBrief.Content, review.Issues, organizationContext))
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
}
