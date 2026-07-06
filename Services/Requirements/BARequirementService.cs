using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

public class BARequirementService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly RequirementTemplateService _templateService;
    private readonly RequirementPromptBuilder _promptBuilder;
    private readonly RequirementResponseParser _responseParser;
    private readonly BAChatReplyParser _replyParser;
    private readonly RequirementReadinessParser _readinessParser;
    private readonly RequirementDocumentGenerator _documentGenerator;
    private readonly PromptTemplateService _promptTemplateService;
    private readonly SourceContextBuilder _sourceContextBuilder;
    private readonly IProjectArtifactCatalog _artifactCatalog;
    private readonly ConversationMemoryService _memory;
    private readonly UserMemoryService _userMemory;
    private readonly ChecklistGapMemoryService _checklistGapMemory;
    private readonly RequirementCoverageService _coverage;
    private readonly ProductBriefReviewParser _reviewParser;
    private readonly OrganizationContextService _orgContext;

    public BARequirementService(
        AppDbContext db,
        ILlmClient llm,
        RequirementTemplateService templateService,
        RequirementPromptBuilder promptBuilder,
        RequirementResponseParser responseParser,
        BAChatReplyParser replyParser,
        RequirementReadinessParser readinessParser,
        RequirementDocumentGenerator documentGenerator,
        PromptTemplateService promptTemplateService,
        SourceContextBuilder sourceContextBuilder,
        IProjectArtifactCatalog artifactCatalog,
        ConversationMemoryService memory,
        UserMemoryService userMemory,
        ChecklistGapMemoryService checklistGapMemory,
        RequirementCoverageService coverage,
        ProductBriefReviewParser reviewParser,
        OrganizationContextService orgContext)
    {
        _db = db;
        _llm = llm;
        _templateService = templateService;
        _promptBuilder = promptBuilder;
        _responseParser = responseParser;
        _replyParser = replyParser;
        _readinessParser = readinessParser;
        _documentGenerator = documentGenerator;
        _promptTemplateService = promptTemplateService;
        _sourceContextBuilder = sourceContextBuilder;
        _artifactCatalog = artifactCatalog;
        _memory = memory;
        _userMemory = userMemory;
        _checklistGapMemory = checklistGapMemory;
        _coverage = coverage;
        _reviewParser = reviewParser;
        _orgContext = orgContext;
    }

    public async Task<ChatWithBAResult> ChatAsync(Guid projectId, string userMessage, CancellationToken cancellationToken = default)
    {
        // Validate the project up front: writing an AgentConversation for a non-existent project would throw an FK DbUpdateException → HTTP 500. Return a status the controller can surface.
        // Tracked (không AsNoTracking) vì bộ nhớ hội thoại ghi thẳng ConversationSummary/SummarizedTurnCount lên entity này.
        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return ChatWithBAResult.ProjectNotFound;

        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken);

        // A missing BA agent / model is a configuration problem, not an exceptional crash: report
        // it as a result so Chat can show a friendly message instead of a 500.
        if (ba?.AiModel == null)
            return ChatWithBAResult.BaNotConfigured;

        var model = ba.AiModel;

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "user",
            Message = userMessage,
            TokenUsed = TokenEstimator.Estimate(userMessage)
        });
        await _db.SaveChangesAsync(cancellationToken);

        // Bộ nhớ hội thoại: cửa sổ lượt gần nhất (gửi nguyên văn) + đoạn tóm tắt dài hạn các lượt cũ đã
        // được gộp dần. Giữ ngữ cảnh hội thoại dài mà prompt không phình token. Xem ConversationMemoryService.
        var memory = await _memory.LoadAsync(project, ba, model, cancellationToken);
        var recent = memory.RecentTurns;

        // Bộ nhớ CẤP USER: hồ sơ bền về chính người dùng, gom xuyên các dự án của họ. Chắt lọc DẦN theo lô
        // rồi nạp lại ở mọi cuộc để BA "càng nói càng hiểu user". Xem UserMemoryService.
        var userMemory = await _userMemory.UpdateAndLoadAsync(project, ba, model, cancellationToken);

        // Bản đồ bao phủ yêu cầu: gộp lượt user vừa lưu (và lượt BA trước đó) vào bảng trạng thái 12 nhóm
        // thông tin, rồi nạp cho BA chọn câu hỏi kế tiếp — phỏng vấn "theo bản đồ" thay vì tuyến tính.
        // Cập nhật ở TỪNG lượt (không batch) vì bản đồ phải tươi mới dẫn được câu hỏi; fail-open khi lỗi.
        var coverageMap = await _coverage.UpdateAndLoadAsync(project, ba, model, cancellationToken);

        // Tài liệu nguồn (ảnh/PDF) của project: gắn vào ĐÚNG lượt user mới nhất (một lần) để BA "thấy" khi trả lời,
        // tránh gửi lại ảnh ở mọi lượt (đốt token). Model không vision ⇒ builder chỉ trả phần text bóc từ PDF.
        var sources = await _db.ProjectSourceFiles
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        var sourceContents = _sourceContextBuilder.Build(sources, model.SupportsVision);
        var lastUserIndex = recent.FindLastIndex(c => c.Role != "assistant");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BA/requirement-chat.v3.md"))
        };
        // Bối cảnh tổ chức Bosch render từ dữ liệu HR thật (OrgUnits/Associates, có cache) + đơn vị yêu cầu
        // của dự án (nếu đã gắn lúc tạo project): BA hiểu ngay tên phòng ban/chức danh người dùng nhắc tới,
        // gợi ý bằng tên phòng thật và hỏi luồng duyệt đúng ngôn ngữ manager/HoD. Fail-open: chưa có dữ
        // liệu ⇒ bỏ qua, chat như cũ. Xem OrganizationContextService.
        var organizationContext = OrganizationContextService.Combine(
            await _orgContext.BuildBaContextAsync(cancellationToken),
            await _orgContext.BuildProjectUnitNoteAsync(project.OrgUnitCode, cancellationToken));
        if (!string.IsNullOrWhiteSpace(organizationContext))
        {
            messages.Add(new ChatMessage(ChatRole.System, organizationContext));
        }
        // Checklist bổ sung được BA rút kinh nghiệm từ các dự án TRƯỚC (của bất kỳ ai) — nạp cho MỌI dự án
        // mới để hỏi kỹ hơn ngay từ đầu, bù cho những nhóm câu hỏi mà checklist tĩnh ban đầu chưa lường tới.
        // Xem ChecklistGapMemoryService.
        if (!string.IsNullOrWhiteSpace(ba.LearnedChecklistNotes))
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## Checklist bổ sung (rút kinh nghiệm từ các dự án trước — chủ động hỏi thêm các mục này nếu liên quan)\n"
                + ba.LearnedChecklistNotes));
        }
        // Hồ sơ người dùng (nếu có): nạp như một system message nền để BA hiểu user ngay từ lượt đầu, kể cả
        // ở dự án mới. Đây là điều tạo cảm giác "càng nói chuyện càng hiểu mình".
        if (!string.IsNullOrWhiteSpace(userMemory))
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## Hồ sơ người dùng (đúc kết từ các lần trao đổi trước — dùng để hiểu & phục vụ đúng ý người dùng, KHÔNG nhắc lại như thể vừa được kể)\n"
                + userMemory));
        }
        // Đính kèm bộ nhớ dài hạn (nếu có) như một system message nền — BA nhớ các lượt cũ đã lược bớt
        // mà không phải đọc lại nguyên văn.
        if (!string.IsNullOrWhiteSpace(memory.Summary))
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## Bộ nhớ hội thoại (tóm tắt các lượt CŨ đã lược bớt để tiết kiệm token — dùng làm ngữ cảnh nền)\n"
                + memory.Summary));
        }
        // Bản đồ bao phủ (nếu có): la bàn để BA chọn câu hỏi kế tiếp — ưu tiên nhóm ★ chưa rõ, không hỏi
        // lại nhóm đã [RÕ]. Prompt requirement-chat.v3 hướng dẫn cách dùng heading này.
        if (!string.IsNullOrWhiteSpace(coverageMap))
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## Bản đồ bao phủ yêu cầu (trạng thái khai thác từng nhóm thông tin — dùng để chọn câu hỏi kế tiếp, KHÔNG hỏi lại nhóm đã [RÕ])\n"
                + coverageMap));
        }
        for (var i = 0; i < recent.Count; i++)
        {
            var c = recent[i];
            var isAssistant = c.Role == "assistant";
            // Lượt cũ của BA được "dựng lại" đúng JSON {message, suggestions}. Nếu chỉ đưa text thuần,
            // model thấy phản hồi trước của mình là văn xuôi và bắt chước → bỏ JSON từ lượt 2 trở đi,
            // mất luôn gợi ý. Đưa lại đúng format giúp model giữ JSON ở mọi lượt.
            var text = isAssistant ? BuildAssistantContext(c) : c.Message;

            if (!isAssistant && i == lastUserIndex && sourceContents.Count > 0)
            {
                var contents = new List<AIContent> { new TextContent(text) };
                contents.AddRange(sourceContents);
                messages.Add(new ChatMessage(ChatRole.User, contents));
            }
            else
            {
                messages.Add(new ChatMessage(isAssistant ? ChatRole.Assistant : ChatRole.User, text));
            }
        }

        // BA được nhắc trả JSON {message, suggestions}: dùng structured output khi model được bật, ngược lại
        // parser luôn fallback an toàn về text thuần.
        var (callResult, structuredReply) = await _llm.ChatStructuredAsync<BAChatReply>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAChat"), cancellationToken: cancellationToken);

        // Surface a failure as a clearly-labelled assistant turn instead of a 500, but never present an API error as if it were a normal BA answer.
        string reply;
        string? suggestionsJson = null;
        if (!callResult.IsSuccess)
        {
            // Tiền tố dùng chung với ConversationTranscriptBuilder để transcript tổng hợp yêu cầu lọc
            // được các lượt lỗi này ra.
            reply = $"{ConversationTranscriptBuilder.LlmFailurePrefix}, chưa thể trả lời. Chi tiết: {callResult.ErrorMessage ?? callResult.Content}";
        }
        else
        {
            var parsedReply = structuredReply ?? _replyParser.Parse(callResult.Content);
            reply = string.IsNullOrWhiteSpace(parsedReply.Message)
                ? "Đã ghi nhận. Bạn có thể bổ sung thêm yêu cầu, hoặc bấm \"Write Requirement\" để tạo tài liệu."
                : parsedReply.Message;

            // Lưu suggestions tách riêng (JSON) để UI render chip; chỉ set khi thực sự có gợi ý.
            if (parsedReply.Suggestions.Count > 0)
                suggestionsJson = JsonSerializer.Serialize(parsedReply.Suggestions);

            // Lượt MỜI bấm "Write Requirement" phải qua ĐÚNG cổng readiness của bước sinh tài liệu NGAY
            // TẠI ĐÂY, trước khi người dùng nhìn thấy lời mời. Không kiểm ở đây thì hai "giám khảo" (BA
            // chat tự thấy đủ, gate lúc bấm nút lại chê thiếu) vênh nhau: BA mời bấm nút, người dùng bấm
            // thì bị chặn "cần bổ sung thông tin" — lặp đi lặp lại rất khó chịu. Gate chê thiếu ⇒ thay
            // lời mời bằng chính câu hỏi của gate (hỏi tiếp ngay trong chat, nút vẫn mờ); gate pass ⇒ giữ
            // lời mời và bước sinh tài liệu sẽ KHÔNG chạy lại gate trên cùng transcript (xem
            // GenerateOrUpdateDraftAsync). Vẫn một cổng, một tiêu chuẩn — chỉ chạy sớm hơn.
            if (IsWriteRequirementInvite(reply))
            {
                // Gate phải thấy ĐÚNG transcript mà lần bấm nút sẽ thấy: toàn bộ hội thoại đã lưu (gồm
                // lượt user vừa lưu ở trên) + chính lời mời này (chưa lưu, đính tạm vào cuối).
                var allTurns = await _db.AgentConversations
                    .Where(c => c.ProjectId == projectId)
                    .ToListAsync(cancellationToken);
                allTurns.Add(new AgentConversation { Role = "assistant", Message = reply, CreatedAt = DateTime.UtcNow });

                var readiness = await CheckReadinessAsync(projectId, ba, model,
                    ConversationTranscriptBuilder.Build(allTurns) + BuildSourceBriefNote(sources) + BuildCoverageNote(project),
                    cancellationToken);
                if (!readiness.Ready)
                {
                    reply = string.IsNullOrWhiteSpace(readiness.Message)
                        ? "Mình cần làm rõ thêm vài thông tin trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                        : readiness.Message;
                    suggestionsJson = readiness.Suggestions.Count > 0
                        ? JsonSerializer.Serialize(readiness.Suggestions)
                        : null;
                }
            }
        }

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "assistant",
            Message = reply,
            Suggestions = suggestionsJson,
            TokenUsed = TokenEstimator.Estimate(reply)
        });
        await _db.SaveChangesAsync(cancellationToken);

        return ChatWithBAResult.Ok;
    }

    /// <param name="onProgress">Callback (kind, message, detail) báo tiến độ live cho UI; có thể null khi gọi đồng bộ.</param>
    /// <param name="onToken">Callback nhận từng token nội dung khi model soạn tài liệu, để stream "đang gõ" lên UI.</param>
    /// <param name="workflowRunId">Run liên quan để gắn chi phí token vào đúng workflow run (null nếu gọi ngoài workflow).</param>
    public async Task<RequirementDraftOutcome> GenerateOrUpdateDraftAsync(Guid projectId, Action<string, string, string?>? onProgress = null, Action<string>? onToken = null, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        void Report(string kind, string message, string? detail = null) => onProgress?.Invoke(kind, message, detail);

        Report("setup", "Đang đọc hội thoại…");

        var project = await _db.Projects
            .Include(x => x.Documents)
            .Include(x => x.Conversations)
            .Include(x => x.SourceFiles)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project not found: {projectId}.");

        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken)
            ?? throw new InvalidOperationException(
                "Chưa cấu hình BA agent (RoleKey = BusinessAnalyst). Hãy tạo hoặc khôi phục agent BA trong màn hình Manage Agent.");

        var model = ba.AiModel ?? throw new InvalidOperationException("BA agent model is not configured.");

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
        // sau khi chính cổng này đã pass ngay trong bước chat (ChatAsync) trên đúng transcript hiện tại,
        // và chưa có gì mới kể từ đó. Chạy lại gate vừa tốn một lời gọi vừa có thể "đổi ý" (LLM không
        // tất định) — chính là vòng lặp khó chịu "mời bấm nút xong lại chặn cần bổ sung thông tin". Bỏ
        // qua gate ở nhánh này; van "không giả định" của bước soạn tài liệu (needsClarification bên
        // dưới) vẫn là chốt chặn cuối nên chất lượng tài liệu không đổi.
        if (IsVerifiedInviteLatestTurn(project.Conversations))
        {
            Report("thinking", "Yêu cầu đã được kiểm tra đủ ngay trong bước chat — bắt đầu soạn tài liệu.", conversationTranscript);
        }
        else
        {
            Report("thinking", "Đang kiểm tra mức độ đầy đủ của yêu cầu…", conversationTranscript);
            var readiness = await CheckReadinessAsync(projectId, ba, model,
                conversationTranscript + BuildSourceBriefNote(sources) + BuildCoverageNote(project), cancellationToken);
            if (!readiness.Ready)
            {
                var question = string.IsNullOrWhiteSpace(readiness.Message)
                    ? "Mình cần làm rõ thêm vài thông tin trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                    : readiness.Message;
                var pendingSuggestions = readiness.Suggestions.Count > 0
                    ? JsonSerializer.Serialize(readiness.Suggestions)
                    : null;

                _db.AgentConversations.Add(new AgentConversation
                {
                    ProjectId = projectId,
                    AgentId = ba.Id,
                    Role = "assistant",
                    Message = question,
                    Suggestions = pendingSuggestions,
                    TokenUsed = TokenEstimator.Estimate(question)
                });
                await _db.SaveChangesAsync(cancellationToken);

                Report("final", "Cần bổ sung thông tin trước khi sinh tài liệu — xem câu hỏi trong khung chat.", question);
                return RequirementDraftOutcome.NeedsMoreInfo;
            }
        }

        Report("thinking", "Đang tổng hợp yêu cầu từ hội thoại…", conversationTranscript);

        // Bối cảnh tổ chức + đơn vị yêu cầu: để tài liệu dùng ĐÚNG tên phòng ban/HoD thật (mục phạm vi,
        // stakeholder) thay vì "TBD"/tên bịa. Cùng một khối này được đưa vào cả vòng tự soát/sửa bên dưới
        // để reviewer không coi các tên thật đó là chi tiết "tự thêm ngoài hội thoại".
        var organizationContext = OrganizationContextService.Combine(
            await _orgContext.BuildBaContextAsync(cancellationToken),
            await _orgContext.BuildProjectUnitNoteAsync(project.OrgUnitCode, cancellationToken));

        var prompt = _promptBuilder.BuildProductBrief(
            project,
            conversationTranscript,
            GetDoc(project, _artifactCatalog.ProductBrief.FileName, "draft"),
            organizationContext);

        // Lượt user mang prompt soạn tài liệu + tài liệu nguồn (text/ảnh) đính kèm. Không có nguồn ⇒ chỉ một
        // TextContent, tương đương đường cũ.
        var userContents = new List<AIContent> { new TextContent(prompt) };
        userContents.AddRange(sourceContents);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BA/product-brief.v3.md")),
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

            _db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = projectId,
                AgentId = ba.Id,
                Role = "assistant",
                Message = clarify,
                Suggestions = result.ClarifyingSuggestions.Count > 0
                    ? JsonSerializer.Serialize(result.ClarifyingSuggestions)
                    : null,
                TokenUsed = TokenEstimator.Estimate(clarify)
            });
            await _db.SaveChangesAsync(cancellationToken);

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

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = ba.Id,
            Role = "assistant",
            Message = assistantMessage,
            TokenUsed = TokenEstimator.Estimate(assistantMessage)
        });

        await _db.SaveChangesAsync(cancellationToken);

        // Tài liệu đã sinh thành công ⇒ đây là lúc có bức tranh Q&A đầy đủ để rút "khoảng trống checklist"
        // (thông tin người dùng phải tự nêu ra mà BA chưa từng hỏi), gộp vào hồ sơ chung của Agent BA để
        // MỌI dự án MỚI sau này (của bất kỳ ai) được hỏi kỹ hơn. Chỉ chạy một lần/dự án; fail-open nếu lỗi.
        await _checklistGapMemory.HarvestAsync(project, ba, model, cancellationToken);

        Report("final", "Đã tạo/cập nhật tài liệu.", assistantMessage);
        return RequirementDraftOutcome.Generated;
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

        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken)
            ?? throw new InvalidOperationException(
                "Chưa cấu hình BA agent (RoleKey = BusinessAnalyst). Hãy tạo hoặc khôi phục agent BA trong màn hình Manage Agent.");

        var model = ba.AiModel ?? throw new InvalidOperationException("BA agent model is not configured.");

        var productBrief = GetDoc(project, _artifactCatalog.ProductBrief.FileName, versionName);
        if (string.IsNullOrWhiteSpace(productBrief))
            throw new InvalidOperationException($"Product Brief đã duyệt không tồn tại cho phiên bản {versionName}.");

        var prompt = _promptBuilder.BuildAiDesignSpec(
            project,
            productBrief,
            GetDoc(project, _artifactCatalog.AiDesignSpec.FileName, versionName));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BA/ai-design-spec.v1.md")),
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

        var ba = await _db.Agents
            .Include(x => x.AiModel)
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst, cancellationToken)
            ?? throw new InvalidOperationException(
                "Chưa cấu hình BA agent (RoleKey = BusinessAnalyst). Hãy tạo hoặc khôi phục agent BA trong màn hình Manage Agent.");

        var model = ba.AiModel ?? throw new InvalidOperationException("BA agent model is not configured.");

        // Phiên bản requirement đã duyệt mới nhất (V{n}). Không có ⇒ chưa Approve, không thể sinh tài liệu kỹ thuật.
        var latestVersion = project.Documents
            .Where(x => x.IsApproved && x.VersionName.StartsWith("V"))
            .Select(x => x.VersionName)
            .OrderByDescending(v => int.TryParse(v.Replace("V", ""), out var n) ? n : 0)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Chưa có phiên bản requirement nào được duyệt. Hãy Approve requirement trước khi tạo tài liệu kỹ thuật.");

        var productBrief = GetDoc(project, _artifactCatalog.ProductBrief.FileName, latestVersion);
        var aiDesignSpec = GetDoc(project, _artifactCatalog.AiDesignSpec.FileName, latestVersion);

        Report("thinking", "Đang soạn tài liệu kỹ thuật từ Product Brief & AI Design Spec đã duyệt…");

        // BRD/SRS có mục stakeholder/đơn vị liên quan: đưa bối cảnh tổ chức + đơn vị yêu cầu để các mục đó
        // mang tên phòng ban/HoD thật từ HR thay vì "TBD".
        var organizationContext = OrganizationContextService.Combine(
            await _orgContext.BuildBaContextAsync(cancellationToken),
            await _orgContext.BuildProjectUnitNoteAsync(project.OrgUnitCode, cancellationToken));

        var prompt = _promptBuilder.BuildTechnicalDocs(
            project,
            productBrief,
            aiDesignSpec,
            GetDoc(project, "BRD.docx", latestVersion),
            GetDoc(project, "SRS.docx", latestVersion),
            GetDoc(project, "FSD.docx", latestVersion),
            GetDoc(project, "UserStories.docx", latestVersion),
            brdTemplate,
            srsTemplate,
            fsdTemplate,
            userStoriesTemplate,
            organizationContext,
            revisionFeedback);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BA/technical-docs.v1.md")),
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

    // Gọi một LLM nhẹ để quyết định đã đủ thông tin cốt lõi soạn tài liệu chưa. Fail-open: gate lỗi thì
    // cứ cho qua để không chặn cứng việc sinh tài liệu.
    private async Task<RequirementReadiness> CheckReadinessAsync(Guid projectId, Agent ba, AiModel model, string requirementBrief, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BA/requirement-readiness.v3.md")),
            new(ChatRole.User, requirementBrief)
        };

        var (callResult, structuredReadiness) = await _llm.ChatStructuredAsync<RequirementReadiness>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAReadinessCheck"), cancellationToken: cancellationToken);

        if (!callResult.IsSuccess)
            return RequirementReadiness.ProceedDefault;

        return structuredReadiness ?? _readinessParser.Parse(callResult.Content);
    }

    // Lượt BA "mời bấm Write Requirement" — cùng tín hiệu mà UI dùng để làm nổi nút (Index.cshtml đọc
    // Contains tương tự trên lượt BA mới nhất) và BuildAssistantContext dùng để echo cờ ready. Từ khi có
    // cổng readiness chạy ngay trong ChatAsync, một lời mời được LƯU đồng nghĩa gate đã pass trên đúng
    // transcript tại thời điểm đó.
    private static bool IsWriteRequirementInvite(string? message) =>
        message?.Contains("Write Requirement", StringComparison.OrdinalIgnoreCase) ?? false;

    // Lượt CÓ NỘI DUNG mới nhất của hội thoại là lời mời bấm "Write Requirement" của BA ⇒ gate readiness
    // đã pass ở bước chat và chưa có thông tin nào mới kể từ đó (người dùng gõ thêm thì ChatAsync luôn
    // lưu một lượt BA mới đè lên vị trí cuối). Lượt lỗi LLM không bao giờ chứa lời mời nên không cần lọc
    // riêng. Thứ tự CreatedAt rồi Id — như ConversationTranscriptBuilder — vì CreatedAt có thể trùng.
    private static bool IsVerifiedInviteLatestTurn(IEnumerable<AgentConversation> conversations)
    {
        var lastTurn = conversations
            .Where(c => !string.IsNullOrWhiteSpace(c.Message))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .LastOrDefault();
        return lastTurn?.Role == "assistant" && IsWriteRequirementInvite(lastTurn.Message);
    }

    // Dựng lại một lượt BA cũ theo đúng JSON shape mà model được yêu cầu xuất, để củng cố format ở
    // mỗi lượt. Không có việc này, model nhìn các lượt trước là văn xuôi và sẽ bỏ JSON (kèm gợi ý) từ
    // lượt thứ 2. Suggestions hỏng/cũ thì coi như mảng rỗng.
    private static string BuildAssistantContext(AgentConversation c)
    {
        // Parse chung với đường render transcript (ConversationTurnRenderer): null/rỗng/hỏng → mảng rỗng.
        var suggestions = ConversationTurnRenderer.ParseSuggestions(c.Suggestions);

        // "ready" được suy ra từ chính nội dung lượt: prompt ép model hễ mời bấm "Write Requirement" thì
        // đó là lúc đã đủ thông tin, nên message có nhắc nút ⇔ ready. Echo lại cờ này để củng cố format JSON.
        var ready = IsWriteRequirementInvite(c.Message);
        return JsonSerializer.Serialize(new { message = c.Message, suggestions, ready });
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
            new(ChatRole.System, _promptTemplateService.Get("BA/product-brief-review.v2.md")),
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
            new(ChatRole.System, _promptTemplateService.Get("BA/product-brief.v3.md")),
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

    // Bản đồ bao phủ (nếu đã có từ các lượt chat) đính vào lời gọi readiness để gate đối chiếu các nhóm
    // ★ theo trạng thái đã ghi nhận thay vì suy lại toàn bộ từ transcript.
    private static string BuildCoverageNote(Project project)
    {
        if (string.IsNullOrWhiteSpace(project.RequirementCoverageMap))
            return string.Empty;

        return "\n## Bản đồ bao phủ yêu cầu (trạng thái khai thác từng nhóm thông tin)\n"
            + project.RequirementCoverageMap;
    }

    // Tóm tắt (text) tài liệu nguồn để đưa vào lời gọi readiness — vốn là call text-only nên KHÔNG kèm ảnh được;
    // bù lại nêu tên file + trích text (bóc từ PDF) có giới hạn, để gate đừng hỏi lại thứ đã có trong tài liệu.
    private static string BuildSourceBriefNote(List<ProjectSourceFile> sources)
    {
        if (sources.Count == 0)
            return string.Empty;

        const int maxCharsPerFile = 4000;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"(Người dùng đã đính kèm {sources.Count} tài liệu nguồn: {string.Join(", ", sources.Select(s => s.FileName))}.)");
        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s.ExtractedText))
                continue;
            var text = s.ExtractedText!.Length > maxCharsPerFile
                ? s.ExtractedText[..maxCharsPerFile] + "…(đã cắt bớt)"
                : s.ExtractedText;
            sb.AppendLine($"[Nội dung trích từ {s.FileName}]");
            sb.AppendLine(text);
        }
        return sb.ToString();
    }

    private static string GetDoc(Project project, string fileName, string versionName)
    {
        return project.Documents
            .Where(x => x.VersionName == versionName && x.FileName == fileName)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.Content)
            .FirstOrDefault() ?? "";
    }
}
