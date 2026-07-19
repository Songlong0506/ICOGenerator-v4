using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Một lượt chat với BA (luồng đồng bộ phía user): lắp ngữ cảnh (memory hai tầng, hồ sơ user, bản đồ bao
/// phủ, bối cảnh tổ chức, tài liệu nguồn) → gọi LLM → chạy cổng readiness ngay khi BA định mời bấm
/// "Write Requirement" → lưu lượt trả lời. Các bước sinh tài liệu nằm ở
/// <see cref="ProductBriefDraftService"/> và <see cref="RequirementDocsService"/>.
/// </summary>
public class BAChatService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _promptTemplateService;
    private readonly SourceContextBuilder _sourceContextBuilder;
    private readonly BAChatReplyParser _replyParser;
    private readonly ConversationMemoryService _memory;
    private readonly UserMemoryService _userMemory;
    private readonly RequirementCoverageService _coverage;
    private readonly OrganizationContextService _orgContext;
    private readonly RequirementReadinessGate _readinessGate;
    private readonly BAAgentResolver _agentResolver;
    private readonly BAConversationLog _conversationLog;
    private readonly DecisionLogService _decisionLog;
    private readonly InterviewOutlookService _interviewOutlook;
    private readonly ChecklistNoteStore _checklistNotes;
    private readonly IServiceScopeFactory? _scopeFactory;

    public BAChatService(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService promptTemplateService,
        SourceContextBuilder sourceContextBuilder,
        BAChatReplyParser replyParser,
        ConversationMemoryService memory,
        UserMemoryService userMemory,
        RequirementCoverageService coverage,
        OrganizationContextService orgContext,
        RequirementReadinessGate readinessGate,
        BAAgentResolver agentResolver,
        BAConversationLog conversationLog,
        DecisionLogService decisionLog,
        InterviewOutlookService interviewOutlook,
        ChecklistNoteStore checklistNotes,
        IServiceScopeFactory? scopeFactory = null)
    {
        _db = db;
        _llm = llm;
        _promptTemplateService = promptTemplateService;
        _sourceContextBuilder = sourceContextBuilder;
        _replyParser = replyParser;
        _memory = memory;
        _userMemory = userMemory;
        _coverage = coverage;
        _orgContext = orgContext;
        _readinessGate = readinessGate;
        _agentResolver = agentResolver;
        _conversationLog = conversationLog;
        _decisionLog = decisionLog;
        _interviewOutlook = interviewOutlook;
        _checklistNotes = checklistNotes;
        // null (test/không có DI đầy đủ) ⇒ các bước chuẩn bị chạy TUẦN TỰ trên chính scope này —
        // hành vi cũ. Có factory ⇒ chạy SONG SONG, mỗi bước một scope riêng (xem PrepareTurnContextAsync).
        _scopeFactory = scopeFactory;
    }

    /// <param name="onStatus">Callback nhận thông điệp trạng thái ngắn ("BA đang soạn trả lời…") để UI cập nhật dòng "đang suy nghĩ" khi stream.</param>
    /// <param name="onToken">Callback nhận từng đoạn text HIỂN THỊ ĐƯỢC của lời trả lời khi model đang gõ (đã lọc cú pháp JSON qua <see cref="BAChatTokenFilter"/>).</param>
    public async Task<BAChatTurnResult> ChatAsync(Guid projectId, string userMessage, Action<string>? onStatus = null, Action<string>? onToken = null, CancellationToken cancellationToken = default)
    {
        // Validate the project up front: writing an AgentConversation for a non-existent project would throw an FK DbUpdateException → HTTP 500. Return a status the controller can surface.
        // Tracked (không AsNoTracking) vì bộ nhớ hội thoại ghi thẳng ConversationSummary/SummarizedTurnCount lên entity này.
        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return new BAChatTurnResult { Status = ChatWithBAResult.ProjectNotFound };

        // A missing BA agent / model is a configuration problem, not an exceptional crash: report
        // it as a result so Chat can show a friendly message instead of a 500.
        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return new BAChatTurnResult { Status = ChatWithBAResult.BaNotConfigured };

        var model = ba.AiModel!;

        await _conversationLog.AppendAsync(projectId, ba.Id, "user", userMessage, cancellationToken: cancellationToken);

        // Các bước chuẩn bị dưới đây có thể gọi LLM (tóm tắt/bồi hồ sơ/bản đồ bao phủ) — báo trạng thái
        // để người dùng thấy BA "đang làm việc" thay vì spinner câm khi stream.
        onStatus?.Invoke("BA đang đọc lại ngữ cảnh hội thoại…");

        // Ba bước chuẩn bị (bộ nhớ hội thoại + hồ sơ user + bản đồ bao phủ) độc lập với nhau và là phần
        // chậm nhất trước khi BA "đặt bút" — chạy SONG SONG để độ chờ mỗi lượt bằng bước chậm nhất thay vì
        // tổng ba bước. Xem PrepareTurnContextAsync về cách cô lập DbContext.
        var (memory, userMemory, coverageMap) = await PrepareTurnContextAsync(project, ba, model, cancellationToken);
        var recent = memory.RecentTurns;

        // Ba nhánh (khi chạy song song) ghi cột bộ nhớ qua context riêng — đồng bộ lại giá trị bản đồ lên
        // entity đang track để các chỗ đọc phía dưới (BuildCoverageNote, kết quả trả về) thấy bản tươi.
        project.RequirementCoverageMap = coverageMap;

        // Tài liệu nguồn (ảnh/PDF) của project: gắn vào ĐÚNG lượt user mới nhất (một lần) để BA "thấy" khi trả lời,
        // tránh gửi lại ảnh ở mọi lượt (đốt token). Model không vision ⇒ builder chỉ trả phần text bóc từ PDF.
        // Chỉ đọc (builder không ghi gì lên entity) ⇒ AsNoTracking, khỏi track cả ExtractedText dài.
        var sources = await _db.ProjectSourceFiles
            .AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        var sourceContents = _sourceContextBuilder.Build(sources, model.SupportsVision);
        var lastUserIndex = recent.FindLastIndex(c => c.Role != "assistant");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/requirement-chat.v3.md"))
        };
        // Bối cảnh tổ chức Bosch render từ dữ liệu HR thật (OrgUnits/Associates, có cache) + đơn vị yêu cầu
        // của dự án (nếu đã gắn lúc tạo project): BA hiểu ngay tên phòng ban/chức danh người dùng nhắc tới,
        // gợi ý bằng tên phòng thật và hỏi luồng duyệt đúng ngôn ngữ manager/HoD. Fail-open: chưa có dữ
        // liệu ⇒ bỏ qua, chat như cũ. Xem OrganizationContextService.
        var organizationContext = await _orgContext.BuildCombinedContextAsync(project.OrgUnitCode, cancellationToken);
        if (!string.IsNullOrWhiteSpace(organizationContext))
        {
            messages.Add(new ChatMessage(ChatRole.System, organizationContext));
        }
        // Checklist bổ sung được BA rút kinh nghiệm từ các dự án TRƯỚC (của bất kỳ ai) — bucket chung +
        // bucket đúng MIỀN nghiệp vụ của dự án (Project.DomainKey, phân loại nền sau lượt chat đầu) — nạp
        // để hỏi kỹ hơn ngay từ đầu mà không bị nhiễu bởi bài học của miền khác. Xem ChecklistNoteStore.
        var learnedChecklist = await _checklistNotes.BuildForChatAsync(ba, project.DomainKey, cancellationToken);
        if (!string.IsNullOrWhiteSpace(learnedChecklist))
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## Checklist bổ sung (rút kinh nghiệm từ các dự án trước — chủ động hỏi thêm các mục này nếu liên quan)\n"
                + learnedChecklist));
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

        onStatus?.Invoke("BA đang soạn câu trả lời…");

        // BA được nhắc trả JSON {message, suggestions}: dùng structured output khi model được bật, ngược lại
        // parser luôn fallback an toàn về text thuần. Khi có onToken, luồng token thô (cú pháp JSON) được
        // lọc qua BAChatTokenFilter để chỉ phần message hiển thị được stream lên UI; đường structured
        // output vốn không stream nên callback đơn giản là không được gọi — UI vẫn nhận bản chốt ở done.
        var tokenFilter = onToken == null ? null : new BAChatTokenFilter(onToken);
        var (callResult, structuredReply) = await _llm.ChatStructuredAsync<BAChatReply>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAChat"),
            tokenFilter == null ? null : tokenFilter.Feed, cancellationToken);

        // Surface a failure as a clearly-labelled assistant turn instead of a 500, but never present an API error as if it were a normal BA answer.
        string reply;
        string? suggestionsJson = null;
        var suggestionsMultiSelect = false;
        var flowDiagram = new List<FlowStep>();
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
            {
                suggestionsJson = JsonSerializer.Serialize(parsedReply.Suggestions);
                suggestionsMultiSelect = parsedReply.MultiSelect;
            }

            // Sơ đồ luồng chỉ có nghĩa ở lượt mời "Write Requirement"; giữ lại đây, nhánh gate bên dưới
            // sẽ xóa nếu lời mời bị thay bằng câu hỏi (khi đó chưa nên vẽ luồng vì còn thiếu thông tin).
            flowDiagram = parsedReply.FlowDiagram ?? new List<FlowStep>();

            // Lượt MỜI bấm "Write Requirement" phải qua ĐÚNG cổng readiness của bước sinh tài liệu NGAY
            // TẠI ĐÂY, trước khi người dùng nhìn thấy lời mời. Không kiểm ở đây thì hai "giám khảo" (BA
            // chat tự thấy đủ, gate lúc bấm nút lại chê thiếu) vênh nhau: BA mời bấm nút, người dùng bấm
            // thì bị chặn "cần bổ sung thông tin" — lặp đi lặp lại rất khó chịu. Gate chê thiếu ⇒ thay
            // lời mời bằng chính câu hỏi của gate (hỏi tiếp ngay trong chat, nút vẫn mờ); gate pass ⇒ giữ
            // lời mời và bước sinh tài liệu sẽ KHÔNG chạy lại gate trên cùng transcript (xem
            // ProductBriefDraftService.GenerateOrUpdateDraftAsync). Vẫn một cổng, một tiêu chuẩn — chỉ
            // chạy sớm hơn.
            if (RequirementReadinessGate.IsWriteRequirementInvite(reply))
            {
                onStatus?.Invoke("Đang kiểm tra đã khai thác đủ thông tin chưa…");

                // Gate phải thấy ĐÚNG transcript mà lần bấm nút sẽ thấy: toàn bộ hội thoại đã lưu (gồm
                // lượt user vừa lưu ở trên) + chính lời mời này (chưa lưu, đính tạm vào cuối — chỉ vào
                // list cục bộ, không vào change tracker ⇒ AsNoTracking cho cả lượt đọc này).
                var allTurns = await _db.AgentConversations
                    .AsNoTracking()
                    .Where(c => c.ProjectId == projectId)
                    .ToListAsync(cancellationToken);
                allTurns.Add(new AgentConversation { Role = "assistant", Message = reply, CreatedAt = DateTime.UtcNow });

                var readiness = await _readinessGate.CheckAsync(projectId, ba, model,
                    ConversationTranscriptBuilder.Build(allTurns)
                        + RequirementReadinessGate.BuildSourceBriefNote(sources)
                        + RequirementReadinessGate.BuildCoverageNote(project),
                    cancellationToken);
                if (!readiness.Ready)
                {
                    reply = string.IsNullOrWhiteSpace(readiness.Message)
                        ? "Mình cần làm rõ thêm vài thông tin trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                        : readiness.Message;
                    suggestionsJson = readiness.Suggestions.Count > 0
                        ? JsonSerializer.Serialize(readiness.Suggestions)
                        : null;
                    // Câu hỏi của gate là câu hỏi đơn thông thường — không giữ cờ multi của lời mời bị thay.
                    suggestionsMultiSelect = false;
                    // Lời mời bị thay bằng câu hỏi ⇒ chưa đủ thông tin, không vẽ sơ đồ luồng nữa.
                    flowDiagram = new List<FlowStep>();
                }
            }
            else
            {
                // Sơ đồ luồng chỉ dành cho lượt MỜI bấm nút; lượt hỏi thường mà model lỡ kèm luồng thì bỏ.
                flowDiagram = new List<FlowStep>();
            }
        }

        var flowDiagramJson = flowDiagram.Count > 0 ? JsonSerializer.Serialize(flowDiagram) : null;
        await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", reply, suggestionsJson, suggestionsMultiSelect, flowDiagramJson, cancellationToken);

        // Trả bản CHỐT (đúng bản vừa lưu) để endpoint streaming render tại chỗ — bản preview đã stream
        // có thể khác (vd lời mời bị gate thay bằng câu hỏi), client luôn thay preview bằng bản này.
        return new BAChatTurnResult
        {
            Status = ChatWithBAResult.Ok,
            Reply = reply,
            Suggestions = string.IsNullOrEmpty(suggestionsJson)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(suggestionsJson) ?? new List<string>(),
            InvitesWriteRequirement = RequirementReadinessGate.IsWriteRequirementInvite(reply),
            SuggestionsMultiSelect = suggestionsMultiSelect,
            // Bản đồ ở thời điểm này đã gộp tới lượt user mới nhất (cập nhật đầu lượt); lượt BA vừa trả
            // lời sẽ được gộp ở lượt sau — đủ tươi cho panel tiến độ.
            Coverage = CoverageMapParser.Parse(project.RequirementCoverageMap).ToList(),
            // "Điều đã chốt" KHÔNG còn chặn đường trả về (một lời gọi LLM ~vài giây mỗi lượt): frame done
            // mang bản đang lưu, bản gộp lượt mới do UpdateDecisionsAsync đẩy ở frame phụ sau done.
            Decisions = DecisionLogService.ParseItems(project.DecisionLog).ToList(),
            FlowDiagram = flowDiagram
        };
    }

    /// <summary>
    /// Gộp các lượt chat mới vào nhật ký "Điều đã chốt" rồi trả bản hiện hành. Tách khỏi
    /// <see cref="ChatAsync"/> để chạy SAU khi user đã nhận câu trả lời (frame done): panel cập nhật
    /// trễ vài giây không sao, còn mỗi lượt chat nhanh hơn đúng một lời gọi LLM. Fail-open toàn phần.
    /// </summary>
    public async Task<IReadOnlyList<string>> UpdateDecisionsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return Array.Empty<string>();

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return DecisionLogService.ParseItems(project.DecisionLog).ToList();

        var decisionLog = await _decisionLog.UpdateAndLoadAsync(project, ba, ba.AiModel!, cancellationToken);
        return DecisionLogService.ParseItems(decisionLog).ToList();
    }

    /// <summary>
    /// Gộp lượt chat mới vào "triển vọng phỏng vấn" (điểm cần làm rõ + màn hình dự kiến + ví dụ tính thử
    /// đã xác nhận) rồi trả bản hiện hành. Như <see cref="UpdateDecisionsAsync"/>: gọi ở HẬU KỲ lượt chat
    /// (sau frame done) để lời gọi LLM này không cộng vào độ chờ. Fail-open toàn phần.
    /// </summary>
    public async Task<InterviewOutlook> UpdateInterviewOutlookAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return new InterviewOutlook();

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return InterviewOutlookService.Current(project);

        return await _interviewOutlook.UpdateAndLoadAsync(project, ba, ba.AiModel!, cancellationToken);
    }

    /// <summary>
    /// Sau khi người dùng upload tài liệu nguồn: BA đọc các nguồn MỚI, tóm tắt những gì hiểu được và xin
    /// xác nhận — thêm MỘT lượt assistant vào hội thoại. Bắt lỗi đọc-nhầm-tài-liệu ngay đầu vào thay vì để
    /// nó thấm vào Product Brief. Fail-open toàn phần: chưa cấu hình BA / model không vision với ảnh /
    /// không có nguồn / lời gọi lỗi ⇒ không thêm lượt nào (trả false), upload vẫn thành công như cũ.
    /// </summary>
    public async Task<bool> AcknowledgeSourcesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
            if (project == null)
                return false;

            var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
            if (ba == null)
                return false;
            var model = ba.AiModel!;

            var sources = await _db.ProjectSourceFiles
                .AsNoTracking()
                .Where(s => s.ProjectId == projectId)
                .OrderBy(s => s.CreatedAt)
                .ToListAsync(cancellationToken);
            var sourceContents = _sourceContextBuilder.Build(sources, model.SupportsVision);
            if (sourceContents.Count == 0)
                return false; // không có gì đọc được (chưa nguồn / PDF scan + model không vision).

            var userContent = new List<AIContent> { new TextContent("Đây là các tài liệu nguồn tôi vừa đính kèm. Bạn đọc giúp và tóm tắt lại cách hiểu để tôi xác nhận nhé.") };
            userContent.AddRange(sourceContents);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/source-ack.v1.md")),
                new(ChatRole.User, userContent)
            };

            var (callResult, parsed) = await _llm.ChatStructuredAsync<BAChatReply>(
                model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BASourceAck"),
                cancellationToken: cancellationToken);
            if (!callResult.IsSuccess)
                return false;

            var reply = parsed ?? _replyParser.Parse(callResult.Content);
            if (string.IsNullOrWhiteSpace(reply.Message))
                return false;

            var suggestionsJson = reply.Suggestions.Count > 0 ? JsonSerializer.Serialize(reply.Suggestions) : null;
            await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", reply.Message.Trim(), suggestionsJson, reply.MultiSelect, cancellationToken: cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false; // bước phụ trợ — lỗi thì bỏ qua, không làm hỏng upload.
        }
    }

    /// <summary>Kết quả 3 bước chuẩn bị ngữ cảnh của một lượt chat.</summary>
    private async Task<(ConversationMemoryService.Memory Memory, string? UserMemory, string? CoverageMap)> PrepareTurnContextAsync(
        Project project, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        // Không có scope factory (unit test dựng tay) ⇒ tuần tự trên scope hiện tại — hành vi cũ.
        if (_scopeFactory == null)
        {
            var seqMemory = await _memory.LoadAsync(project, ba, model, cancellationToken);
            var seqUserMemory = await _userMemory.UpdateAndLoadAsync(project, ba, model, cancellationToken);
            var seqCoverage = await _coverage.UpdateAndLoadAsync(project, ba, model, cancellationToken);
            return (seqMemory, seqUserMemory, seqCoverage);
        }

        // DbContext không thread-safe nên mỗi nhánh chạy trong MỘT DI scope riêng: tự load Project/BA
        // từ context của scope đó rồi gọi đúng service tương ứng. Ba nhánh ghi các CỘT KHÁC NHAU trên
        // dòng Projects (summary / con trỏ user-memory / bản đồ bao phủ) nên không giẫm nhau.
        var memoryTask = RunInScopeAsync((sp, prj, agent) =>
            sp.GetRequiredService<ConversationMemoryService>().LoadAsync(prj, agent, agent.AiModel!, cancellationToken), project.Id, cancellationToken);
        var userMemoryTask = RunInScopeAsync((sp, prj, agent) =>
            sp.GetRequiredService<UserMemoryService>().UpdateAndLoadAsync(prj, agent, agent.AiModel!, cancellationToken), project.Id, cancellationToken);
        var coverageTask = RunInScopeAsync((sp, prj, agent) =>
            sp.GetRequiredService<RequirementCoverageService>().UpdateAndLoadAsync(prj, agent, agent.AiModel!, cancellationToken), project.Id, cancellationToken);

        await Task.WhenAll(memoryTask, userMemoryTask, coverageTask);
        return (memoryTask.Result, userMemoryTask.Result, coverageTask.Result);
    }

    // Chạy một bước chuẩn bị trong scope DI riêng (DbContext riêng). Project/BA load lại từ context của
    // scope để entity được track đúng chỗ; BA thiếu cấu hình đã bị chặn từ đầu ChatAsync nên không xảy ra.
    private async Task<T> RunInScopeAsync<T>(Func<IServiceProvider, Project, Agent, Task<T>> action, Guid projectId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId, cancellationToken);
        var ba = await scope.ServiceProvider.GetRequiredService<BAAgentResolver>().FindConfiguredAsync(cancellationToken)
                 ?? throw new InvalidOperationException("BA agent is no longer configured.");
        return await action(scope.ServiceProvider, project, ba);
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
        var ready = RequirementReadinessGate.IsWriteRequirementInvite(c.Message);
        return JsonSerializer.Serialize(new { message = c.Message, suggestions, multiSelect = c.SuggestionsMultiSelect, ready });
    }
}
