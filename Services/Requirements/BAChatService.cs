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
/// phủ, bối cảnh tổ chức, tài liệu nguồn) → gọi LLM → xét cổng readiness TẤT ĐỊNH trên bản đồ bao phủ
/// ngay khi BA định mời bấm "Write Requirement" → lưu lượt trả lời. Các bước sinh tài liệu nằm ở
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
    private readonly BAAgentResolver _agentResolver;
    private readonly BAConversationLog _conversationLog;
    private readonly DecisionLogService _decisionLog;
    private readonly InterviewOutlookService _interviewOutlook;
    private readonly ChecklistNoteStore _checklistNotes;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly BAChatTurnTracker? _turnTracker;

    /// <summary>
    /// Ngưỡng ân hạn trước khi một lượt đang chờ bị coi là ĐÃ CHẾT khi không có lượt nào đang chạy trong
    /// tiến trình (xem <see cref="BAChatTurnTracker"/>). Chỉ dùng cho các lượt KHÔNG được sổ theo dõi ghi
    /// nhận — tiến trình vừa khởi động lại, hoặc lượt do instance khác chạy — nên đủ rộng để không cắt
    /// ngang một lượt thật, mà vẫn ngắn hơn nhiều so với việc treo màn hình vĩnh viễn.
    /// </summary>
    public static readonly TimeSpan ReplyStaleAfter = TimeSpan.FromMinutes(3);

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
        BAAgentResolver agentResolver,
        BAConversationLog conversationLog,
        DecisionLogService decisionLog,
        InterviewOutlookService interviewOutlook,
        ChecklistNoteStore checklistNotes,
        IServiceScopeFactory? scopeFactory = null,
        BAChatTurnTracker? turnTracker = null)
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
        _agentResolver = agentResolver;
        _conversationLog = conversationLog;
        _decisionLog = decisionLog;
        _interviewOutlook = interviewOutlook;
        _checklistNotes = checklistNotes;
        // null (test/không có DI đầy đủ) ⇒ các bước chuẩn bị chạy TUẦN TỰ trên chính scope này —
        // hành vi cũ. Có factory ⇒ chạy SONG SONG, mỗi bước một scope riêng (xem PrepareTurnContextAsync).
        _scopeFactory = scopeFactory;
        // null (test) ⇒ không có sổ theo dõi lượt đang chạy: GetReplyStateAsync chỉ xét tuổi lượt user.
        _turnTracker = turnTracker;
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

        return await RunTurnGuaranteedAsync(project, ba, model, onStatus, onToken, cancellationToken);
    }

    /// <summary>
    /// <see cref="RunTurnAsync"/> + BẢO ĐẢM hội thoại không bao giờ kết thúc bằng một lượt user "cụt".
    /// <para>
    /// Lượt user được lưu TRƯỚC khi gọi LLM (để nó không biến mất khi user F5). Nếu phần sau đó ném ra
    /// ngoài — lỗi mạng/hạ tầng, DbContext hỏng, chạm trần ngân sách, host tắt giữa chừng — thì trước đây
    /// hội thoại nằm lại vĩnh viễn ở trạng thái "lượt cuối là user", tức là
    /// <see cref="GetReplyStateAsync"/> luôn báo pending: màn hình treo ở "BA đang soạn câu trả lời…",
    /// F5 cũng không thoát và không gửi được tin mới. Ở đây mọi ngoại lệ đều được ĐÓNG LƯỢT bằng một lượt
    /// assistant ⚠️ (đúng tiền tố dùng chung) để UI tô đỏ + hiện nút "Thử lại", rồi mới ném tiếp cho
    /// controller xử lý như cũ.
    /// </para>
    /// </summary>
    private async Task<BAChatTurnResult> RunTurnGuaranteedAsync(Project project, Agent ba, AiModel model, Action<string>? onStatus, Action<string>? onToken, CancellationToken cancellationToken)
    {
        try
        {
            return await RunTurnAsync(project, ba, model, onStatus, onToken, cancellationToken);
        }
        catch (Exception ex)
        {
            await TryCloseTurnWithFailureAsync(project.Id, ba.Id, ex);
            throw;
        }
    }

    /// <summary>
    /// Ghi lượt assistant ⚠️ "đóng" một lượt chat vừa vỡ. Best-effort và KHÔNG được ném: nó chạy trên
    /// đường xử lý ngoại lệ, nuốt mất lỗi gốc thì còn tệ hơn. Dùng scope DI riêng khi có (DbContext của
    /// scope hiện tại có thể chính là thứ vừa hỏng); token None vì lượt phải được lưu kể cả khi request
    /// đã bị hủy — đó chính là lúc cần nó nhất.
    /// </summary>
    private async Task TryCloseTurnWithFailureAsync(Guid projectId, Guid baId, Exception ex)
    {
        var message = $"{ConversationTranscriptBuilder.LlmFailurePrefix}, lượt trả lời bị gián đoạn. Chi tiết: {ex.Message}";
        try
        {
            if (_scopeFactory != null)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BAConversationLog>()
                    .AppendAsync(projectId, baId, "assistant", message, cancellationToken: CancellationToken.None);
                return;
            }

            await _conversationLog.AppendAsync(projectId, baId, "assistant", message, cancellationToken: CancellationToken.None);
        }
        catch
        {
            // Ghi lượt đóng cũng hỏng (mất kết nối DB): không còn gì làm được ở đây. Cờ "stale" của
            // GetReplyStateAsync vẫn là lưới an toàn cuối để UI không treo mãi.
        }
    }

    /// <summary>
    /// Cho biết câu trả lời của BA cho lượt hiện tại còn "đang chờ": lượt hội thoại MỚI NHẤT là của
    /// người dùng (role "user") nên BA vẫn đang soạn lượt assistant tương ứng. Một lượt chat luôn kết
    /// thúc bằng một lượt assistant (câu trả lời hoặc thông báo ⚠️) và chạy với CancellationToken.None,
    /// nên dù người dùng F5/đóng tab giữa chừng thì lượt assistant VẪN được sinh và lưu — chỉ là chưa
    /// kịp. UI dùng cờ này để, sau khi tải lại trang giữa lúc BA đang trả lời, hiện lại khung "BA đang
    /// soạn…" rồi chờ câu trả lời được lưu (thay vì để bong bóng trả lời "biến mất" cho tới lần F5 sau).
    /// Global query filter đã loại các lượt đã lưu trữ (ArchivedAt != null) nên chỉ xét hội thoại hiện hành.
    /// <para>
    /// Kèm cờ <see cref="ChatReplyState.Stale"/>: lượt đang chờ đó KHÔNG bao giờ về đích nữa — không có
    /// lượt nào đang chạy trong tiến trình (<see cref="BAChatTurnTracker"/>) và lượt user đã cũ hơn
    /// <see cref="ReplyStaleAfter"/>. Xảy ra khi tiến trình khởi động lại giữa lúc BA đang trả lời, hoặc
    /// lỗi hạ tầng nuốt mất cả lượt ⚠️ đóng lượt. Không có cờ này, UI chờ mãi ⇒ treo màn hình ở
    /// "BA đang soạn câu trả lời…" và chặn luôn việc gửi tin mới, F5 cũng không thoát.
    /// </para>
    /// </summary>
    public async Task<ChatReplyState> GetReplyStateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // Cùng thứ tự ổn định (CreatedAt rồi Id) như RetryLastTurnAsync và mọi chỗ đọc hội thoại khác.
        var lastTurn = await _db.AgentConversations
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Select(c => new { c.Role, c.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (lastTurn?.Role != "user")
            return new ChatReplyState(false, false);

        // Lượt đang chạy thật trong tiến trình này ⇒ cứ chờ, dù đã chờ bao lâu (model chậm vẫn về đích).
        if (_turnTracker?.IsRunning(projectId) == true)
            return new ChatReplyState(true, false);

        // Không ai đang chạy: chỉ kết luận "chết" sau ngưỡng ân hạn, để không cắt ngang lượt vừa mới bắt
        // đầu (sổ theo dõi chưa kịp ghi) hay lượt do instance khác chạy.
        var age = DateTime.UtcNow - lastTurn.CreatedAt;
        return new ChatReplyState(true, age > ReplyStaleAfter);
    }

    /// <summary>
    /// "Thử lại" lượt BA vừa LỖI (lời gọi LLM thất bại được lưu thành thông báo ⚠️): xóa đúng lượt lỗi
    /// cuối rồi chạy lại lượt chat trên transcript hiện có — KHÔNG ghi thêm lượt user nào (câu hỏi của
    /// người dùng vẫn đang nằm cuối hội thoại). Không có gì để thử lại (lượt cuối không phải thông báo
    /// lỗi — ví dụ user đã nhắn thêm, hoặc tab khác đã retry trước) ⇒ trả
    /// <see cref="ChatWithBAResult.NothingToRetry"/> để UI mời tải lại trang thay vì chạy đúp.
    /// <para>
    /// Cũng nhận lượt cuối là USER còn "cụt" (câu trả lời đã chết — xem <see cref="GetReplyStateAsync"/>):
    /// chạy lại đúng lượt đó trên transcript hiện có, không xóa gì và không ghi thêm lượt user. Đây là
    /// đường thoát cho các hội thoại đã kẹt sẵn trong DB.
    /// </para>
    /// </summary>
    public async Task<BAChatTurnResult> RetryLastTurnAsync(Guid projectId, Action<string>? onStatus = null, Action<string>? onToken = null, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return new BAChatTurnResult { Status = ChatWithBAResult.ProjectNotFound };

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return new BAChatTurnResult { Status = ChatWithBAResult.BaNotConfigured };

        // Cùng thứ tự ổn định (CreatedAt rồi Id) như mọi chỗ đọc hội thoại — lấy lượt MỚI NHẤT.
        var lastTurn = await _db.AgentConversations
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastTurn == null)
            return new BAChatTurnResult { Status = ChatWithBAResult.NothingToRetry };

        // Lượt user "cụt": câu trả lời cũ đã chết. Chạy lại nguyên lượt đó, không xóa gì, không ghi thêm
        // lượt user. Việc chặn chạy đúp (khi lượt đó THẬT SỰ đang được trả lời ở nơi khác) nằm ở điểm
        // vào — BAChatTurnTracker.TryBeginExclusive trong ChatStream — vì chỉ ở đó mới phân biệt được
        // "lượt của người khác" với "lượt của chính request này".
        if (lastTurn.Role == "user")
            return await RunTurnGuaranteedAsync(project, ba, ba.AiModel!, onStatus, onToken, cancellationToken);

        if (lastTurn.Role != "assistant"
            || !(lastTurn.Message ?? string.Empty).StartsWith(ConversationTranscriptBuilder.LlmFailurePrefix, StringComparison.Ordinal))
            return new BAChatTurnResult { Status = ChatWithBAResult.NothingToRetry };

        // Xóa hẳn lượt lỗi (nó không phải nội dung yêu cầu — transcript vốn đã lọc bỏ nó) để lượt chạy
        // lại ghi câu trả lời mới vào đúng vị trí cuối hội thoại.
        _db.AgentConversations.Remove(lastTurn);
        await _db.SaveChangesAsync(cancellationToken);

        return await RunTurnGuaranteedAsync(project, ba, ba.AiModel!, onStatus, onToken, cancellationToken);
    }

    /// <summary>
    /// SỬA lượt user vừa gửi rồi trả lời lại: ghi đè nội dung lượt user MỚI NHẤT bằng
    /// <paramref name="newMessage"/>, xóa lượt trả lời tương ứng (nếu đã có) và chạy lại lượt trên
    /// transcript đã sửa.
    /// <para>
    /// Không có đường này, một câu gõ nhầm/nói hụt chỉ có thể "sửa" bằng cách nhắn thêm một câu đính
    /// chính — nhưng bản đồ bao phủ, nhật ký điều đã chốt và bộ nhớ hội thoại đều đã kịp ghi nhận câu
    /// SAI, và chúng gộp lũy tiến nên câu sai đó không bao giờ biến mất khỏi ngữ cảnh.
    /// </para>
    /// <para>
    /// Vì lượt assistant bị xóa làm SỐ LƯỢT giảm đi, mọi con trỏ "đã gộp tới lượt thứ n" phải được kéo
    /// lùi xuống trước lượt user vừa sửa — nếu không, con trỏ vượt quá số lượt hiện có và mọi lượt gộp
    /// sau đó thấy delta rỗng: bản đồ/nhật ký đóng băng vĩnh viễn ở bản dựng từ câu đã bị sửa.
    /// </para>
    /// </summary>
    public async Task<BAChatTurnResult> EditLastUserTurnAsync(Guid projectId, string newMessage, Action<string>? onStatus = null, Action<string>? onToken = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newMessage))
            return new BAChatTurnResult { Status = ChatWithBAResult.NothingToRetry };

        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return new BAChatTurnResult { Status = ChatWithBAResult.ProjectNotFound };

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return new BAChatTurnResult { Status = ChatWithBAResult.BaNotConfigured };

        // Cùng thứ tự ổn định (CreatedAt rồi Id) như mọi chỗ đọc hội thoại khác.
        var turns = await _db.AgentConversations
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (turns.Count == 0)
            return new BAChatTurnResult { Status = ChatWithBAResult.NothingToRetry };

        // Lượt cuối là assistant ⇒ xóa nó (câu trả lời cho câu hỏi cũ), lượt user cần sửa nằm ngay trước.
        // Lượt cuối là user ⇒ câu trả lời chưa/không bao giờ tới, sửa thẳng lượt đó.
        AgentConversation userTurn;
        if (turns[0].Role == "assistant")
        {
            if (turns.Count < 2 || turns[1].Role != "user")
                return new BAChatTurnResult { Status = ChatWithBAResult.NothingToRetry };
            _db.AgentConversations.Remove(turns[0]);
            userTurn = turns[1];
        }
        else
        {
            userTurn = turns[0];
        }

        userTurn.Message = newMessage.Trim();
        userTurn.TokenUsed = TokenEstimator.Estimate(userTurn.Message);
        await _db.SaveChangesAsync(cancellationToken);

        // Kéo lùi mọi con trỏ gộp về TRƯỚC lượt vừa sửa, để các bản đúc kết được dựng lại từ nội dung mới.
        var turnCount = await _db.AgentConversations.CountAsync(c => c.ProjectId == projectId, cancellationToken);
        var beforeEdited = Math.Max(0, turnCount - 1);
        project.CoverageHarvestedTurnCount = Math.Min(project.CoverageHarvestedTurnCount, beforeEdited);
        project.DecisionHarvestedTurnCount = Math.Min(project.DecisionHarvestedTurnCount, beforeEdited);
        project.InterviewOutlookHarvestedTurnCount = Math.Min(project.InterviewOutlookHarvestedTurnCount, beforeEdited);
        project.UserMemoryHarvestedTurnCount = Math.Min(project.UserMemoryHarvestedTurnCount, beforeEdited);
        project.SummarizedTurnCount = Math.Min(project.SummarizedTurnCount, beforeEdited);
        // Cổng soát mâu thuẫn cũng phải soát lại: nội dung vừa đổi có thể chính là vế đang chọi nhau.
        project.ConflictCheckedTurnCount = Math.Min(project.ConflictCheckedTurnCount, beforeEdited);
        await _db.SaveChangesAsync(cancellationToken);

        return await RunTurnGuaranteedAsync(project, ba, ba.AiModel!, onStatus, onToken, cancellationToken);
    }

    // Lõi một lượt trả lời của BA (chuẩn bị ngữ cảnh → gọi LLM → cổng readiness → lưu lượt assistant).
    // Tách khỏi ChatAsync để đường "thử lại lượt lỗi" chạy lại y hệt mà không ghi thêm lượt user.
    private async Task<BAChatTurnResult> RunTurnAsync(Project project, Agent ba, AiModel model, Action<string>? onStatus, Action<string>? onToken, CancellationToken cancellationToken)
    {
        var projectId = project.Id;

        // Các bước chuẩn bị dưới đây có thể gọi LLM (tóm tắt/bồi hồ sơ/bản đồ bao phủ) — báo trạng thái
        // để người dùng thấy BA "đang làm việc" thay vì spinner câm khi stream.
        onStatus?.Invoke("BA đang đọc lại ngữ cảnh hội thoại…");

        // Ba bước chuẩn bị (bộ nhớ hội thoại + hồ sơ user + bản đồ bao phủ) độc lập với nhau và là phần
        // chậm nhất trước khi BA "đặt bút" — chạy SONG SONG để độ chờ mỗi lượt bằng bước chậm nhất thay vì
        // tổng ba bước. Xem PrepareTurnContextAsync về cách cô lập DbContext.
        var (memory, userMemory, coverageUpdate) = await PrepareTurnContextAsync(project, ba, model, cancellationToken);
        var recent = memory.RecentTurns;
        var coverageMap = coverageUpdate.Map;

        // Ba nhánh (khi chạy song song) ghi cột bộ nhớ qua context riêng — đồng bộ lại giá trị bản đồ lên
        // entity đang track để các chỗ đọc phía dưới (BuildCoverageNote, kết quả trả về) thấy bản tươi.
        project.RequirementCoverageMap = coverageMap;

        // Tài liệu nguồn (ảnh/PDF) của project: gắn vào ĐÚNG lượt user mới nhất (một lần) để BA "thấy" khi trả lời,
        // thay vì lặp lại ở mọi message trong lịch sử. Model không vision ⇒ builder chỉ trả phần text bóc từ PDF.
        // Lưu ý chi phí: mỗi lượt chat là một request MỚI, nên nguồn nào còn phải đi bằng ẢNH thì lượt nào
        // cũng upload lại ảnh đó. Thứ chặn việc này là VisionSummary — nguồn đã được BA mô tả nội dung hình
        // thành chữ ở lượt xác nhận tài liệu thì từ đây chỉ còn mang phần chữ (xem SourceContextBuilder).
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
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/requirement-chat.v4.md"))
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
        // lại nhóm đã [RÕ]. Prompt requirement-chat.v4 hướng dẫn cách dùng heading này.
        if (!string.IsNullOrWhiteSpace(coverageMap))
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## Bản đồ bao phủ yêu cầu (trạng thái khai thác từng nhóm thông tin — dùng để chọn câu hỏi kế tiếp)\n"
                + "Nhóm đã [RÕ]: KHÔNG hỏi lại. Nhóm [MỘT PHẦN]: chỉ hỏi ĐÚNG phần ghi sau \"còn thiếu:\", "
                + "KHÔNG phát lại câu hỏi mở đầu của nhóm đó (người dùng đã trả lời phần còn lại rồi).\n"
                + coverageMap));
        }
        // Sổ "đã hỏi rồi": bản đồ ở trên chỉ có độ phân giải theo NHÓM, nên một nhóm chưa [RÕ] dễ khiến
        // model phát lại nguyên văn câu hỏi mở đầu của chính nhóm ấy. Danh sách câu hỏi thật là thứ duy
        // nhất phân biệt được "hỏi tiếp phần còn thiếu" với "hỏi lại điều vừa được trả lời".
        var askedBefore = AskedQuestionHistory.Collect(recent);
        var askedNote = AskedQuestionHistory.BuildNote(askedBefore);
        if (!string.IsNullOrWhiteSpace(askedNote))
        {
            messages.Add(new ChatMessage(ChatRole.System, askedNote));
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
                contents.AddRange(sourceContents.Contents);
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
        var questions = new List<BAChatQuestion>();
        var flowDiagram = new List<FlowStep>();
        if (!callResult.IsSuccess)
        {
            // Tiền tố dùng chung với ConversationTranscriptBuilder để transcript tổng hợp yêu cầu lọc
            // được các lượt lỗi này ra.
            reply = $"{ConversationTranscriptBuilder.LlmFailurePrefix}, chưa thể trả lời. Chi tiết: {callResult.ErrorMessage ?? callResult.Content}";
        }
        else
        {
            // Đường structured output trả thẳng BAChatReply (không qua Parse), nên phải chuẩn hoá RIÊNG:
            // trần "tối đa 4 câu hỏi một lượt" và việc hạ lượt-gộp-một-câu về đường một-câu sống trong
            // Normalize. Bỏ bước này thì các model tốt (đường mặc định) là các model KHÔNG bị chặn.
            var parsedReply = structuredReply != null
                ? _replyParser.Normalize(structuredReply)
                : _replyParser.Parse(callResult.Content);
            reply = string.IsNullOrWhiteSpace(parsedReply.Message)
                ? "Đã ghi nhận. Bạn có thể bổ sung thêm yêu cầu, hoặc bấm \"Write Requirement\" để tạo tài liệu."
                : parsedReply.Message;

            // Lưu suggestions tách riêng (JSON) để UI render chip; chỉ set khi thực sự có gợi ý.
            if (parsedReply.Suggestions.Count > 0)
            {
                suggestionsJson = JsonSerializer.Serialize(parsedReply.Suggestions);
                suggestionsMultiSelect = parsedReply.MultiSelect;
            }

            // Lượt hỏi GỘP (2–4 câu độc lập): Normalize đã đảm bảo hoặc có Questions, hoặc có
            // Suggestions — không bao giờ cả hai.
            questions = parsedReply.Questions;

            // Sơ đồ luồng chỉ có nghĩa ở lượt mời "Write Requirement"; giữ lại đây, nhánh gate bên dưới
            // sẽ xóa nếu lời mời bị thay bằng câu hỏi (khi đó chưa nên vẽ luồng vì còn thiếu thông tin).
            flowDiagram = parsedReply.FlowDiagram ?? new List<FlowStep>();

            // PHANH CHỐNG HỎI LẠI (tất định). Prompt đã cấm phát lại câu cũ, nhưng bản đồ bao phủ — thứ
            // dẫn dắt lượt hỏi — chỉ có độ phân giải theo NHÓM: một dòng chưa đạt chuẩn [RÕ] (hoặc một
            // lượt chắt lọc bản đồ hỏng, giữ nguyên bản cũ) là đủ để model phát lại nguyên văn cả cụm câu
            // hỏi của lượt trước, kèm chip gợi ý chính là câu trả lời người dùng vừa gõ. Ở đây câu trùng
            // bị LOẠI khỏi lượt trả lời trước khi nó kịp lên màn hình.
            var askedKeys = AskedQuestionHistory.Keys(askedBefore);
            var reopenedGroups = AskedQuestionHistory.ReopenedGroups(CoverageMapParser.Parse(project.RequirementCoverageMap));
            if (questions.Count > 0)
            {
                var kept = questions
                    .Where(q => AskedQuestionHistory.IsExempt(q, reopenedGroups)
                                || !AskedQuestionHistory.IsRepeat(q.Question, askedKeys))
                    .ToList();

                if (kept.Count < questions.Count)
                {
                    // Câu dẫn của lượt gộp thường tự đếm ("dưới đây là 4 câu xác nhận") nên bỏ bớt câu là
                    // nó nói sai — thay bằng câu dẫn trung tính. Còn đúng một câu thì để Message rỗng cho
                    // Normalize nâng chính câu hỏi lên làm nội dung lượt (đường một-câu).
                    var trimmed = _replyParser.Normalize(new BAChatReply
                    {
                        Message = kept.Count >= 2 ? "Cảm ơn anh/chị. Mình hỏi thêm mấy điểm sau nhé:" : string.Empty,
                        Questions = kept
                    });

                    // Không còn câu nào MỚI để hỏi: lượt này lẽ ra rỗng. Thay bằng bước kế tiếp TẤT ĐỊNH
                    // suy từ bản đồ (hỏi đúng nhóm còn thiếu, hoặc mời bấm nút khi bản đồ đã đủ) — im lặng
                    // hoặc để nguyên câu dẫn cụt đều tệ hơn.
                    var (message, followUpSuggestions) = kept.Count == 0
                        ? BuildFollowUpAfterRepeat(project.RequirementCoverageMap)
                        : (trimmed.Message, trimmed.Suggestions);

                    reply = message;
                    questions = trimmed.Questions;
                    suggestionsJson = followUpSuggestions.Count > 0 ? JsonSerializer.Serialize(followUpSuggestions) : null;
                    suggestionsMultiSelect = trimmed.MultiSelect && followUpSuggestions.Count > 0;
                    flowDiagram = new List<FlowStep>();
                }
            }
            else if (parsedReply.Suggestions.Count > 0
                     && !RequirementReadinessGate.IsWriteRequirementInvite(reply)
                     && AskedQuestionHistory.IsRepeat(reply, askedKeys))
            {
                // Lượt hỏi MỘT câu, và chính câu đó đã hỏi rồi (Message chở câu hỏi ở đường này).
                var (message, followUpSuggestions) = BuildFollowUpAfterRepeat(project.RequirementCoverageMap);
                reply = message;
                suggestionsJson = followUpSuggestions.Count > 0 ? JsonSerializer.Serialize(followUpSuggestions) : null;
                suggestionsMultiSelect = false;
                flowDiagram = new List<FlowStep>();
            }

            // Lượt MỜI bấm "Write Requirement" phải qua cổng readiness TẤT ĐỊNH ngay tại đây, trước khi
            // người dùng nhìn thấy lời mời: ready suy thẳng từ bản đồ bao phủ (đã gộp tới lượt user mới
            // nhất ở đầu lượt này) — cùng dữ liệu mà panel "Tiến độ khai thác" render, nên panel, lời mời
            // và nút KHÔNG THỂ vênh nhau. Bản đồ chưa đủ (kể cả khi lượt gộp lỗi giữ bản cũ — fail-closed,
            // lượt sau gộp bù) ⇒ thay lời mời bằng câu hỏi nêu đúng nhóm còn thiếu, nút vẫn mờ; đủ ⇒ giữ
            // lời mời và bước sinh tài liệu KHÔNG xét lại trên cùng transcript (xem
            // ProductBriefDraftService.GenerateOrUpdateDraftAsync). Một nguồn chân lý, một tiêu chuẩn.
            if (RequirementReadinessGate.IsWriteRequirementInvite(reply))
            {
                var readiness = RequirementReadinessGate.Evaluate(project.RequirementCoverageMap);
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
                    // …và cũng không giữ thẻ hỏi gộp: nội dung hiển thị giờ là câu hỏi của gate, để lại
                    // thẻ cũ thì màn hình có hai lượt hỏi khác nhau chồng lên nhau.
                    questions = new List<BAChatQuestion>();
                    // Lời mời bị thay bằng câu hỏi ⇒ chưa đủ thông tin, không vẽ sơ đồ luồng nữa.
                    flowDiagram = new List<FlowStep>();
                }
                else
                {
                    // Lời mời ĐƯỢC GIỮ: lượt này là lời mời bấm nút, không phải lượt hỏi. Một lời mời kèm
                    // thẻ hỏi là tự mâu thuẫn ("không còn gì để hỏi" + 3 câu hỏi), và ở đúng lượt mà cổng
                    // vừa mở — người dùng sẽ trả lời thẻ đó rồi tự hỏi vì sao mình vẫn chưa được viết.
                    questions = new List<BAChatQuestion>();
                }
            }
            else
            {
                // Sơ đồ luồng chỉ dành cho lượt MỜI bấm nút; lượt hỏi thường mà model lỡ kèm luồng thì bỏ.
                flowDiagram = new List<FlowStep>();
            }
        }

        var flowDiagramJson = flowDiagram.Count > 0 ? JsonSerializer.Serialize(flowDiagram) : null;
        var questionsJson = questions.Count > 0 ? JsonSerializer.Serialize(questions) : null;
        await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", reply, suggestionsJson, suggestionsMultiSelect, flowDiagramJson, questionsJson: questionsJson, cancellationToken: cancellationToken);

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
            Questions = questions,
            // Bản đồ ở thời điểm này đã gộp tới lượt user mới nhất (cập nhật đầu lượt); lượt BA vừa trả
            // lời sẽ được gộp ở lượt sau — đủ tươi cho panel tiến độ.
            Coverage = CoverageMapParser.Parse(project.RequirementCoverageMap).ToList(),
            // "Điều đã chốt" KHÔNG còn chặn đường trả về (một lời gọi LLM ~vài giây mỗi lượt): frame done
            // mang bản đang lưu, bản gộp lượt mới do UpdateDecisionsAsync đẩy ở frame phụ sau done.
            Decisions = DecisionLogService.ParseItems(project.DecisionLog).ToList(),
            FlowDiagram = flowDiagram,
            // Bản đồ KHÔNG gộp được lượt này (đã thử lại): panel tiến độ đang hiển thị bản cũ và BA vừa
            // dẫn lượt bằng bản cũ đó. Nói thẳng ra thay vì để người dùng tự đoán vì sao tiến độ đứng im.
            CoverageStale = coverageUpdate.DistillFailed
        };
    }

    /// <summary>
    /// Bước kế tiếp TẤT ĐỊNH khi mọi câu hỏi của lượt vừa rồi đều là câu đã hỏi: hỏi đúng nhóm mà bản đồ
    /// bao phủ còn ghi thiếu, hoặc — khi bản đồ đã đủ theo cùng cổng readiness dùng ở mọi nơi khác — mời
    /// bấm "Write Requirement". Không bao giờ trả về lượt rỗng: một lượt câm sau khi người dùng vừa trả
    /// lời còn khó hiểu hơn cả việc bị hỏi lại.
    /// </summary>
    private static (string Message, List<string> Suggestions) BuildFollowUpAfterRepeat(string? coverageMap)
    {
        var readiness = RequirementReadinessGate.Evaluate(coverageMap);
        if (!readiness.Ready)
        {
            return (string.IsNullOrWhiteSpace(readiness.Message)
                ? "Mình cần làm rõ thêm vài thông tin trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                : readiness.Message, readiness.Suggestions.ToList());
        }

        // Bản đồ đã đủ ⇒ lời mời này đi qua đúng cổng mà nhánh dưới sẽ xét lại, nên không thể là lời mời
        // sớm. Không kèm gợi ý: hành động duy nhất lúc này là bấm nút thật trên giao diện.
        return ("Mình đã ghi nhận đủ các nhóm thông tin cần thiết và không còn câu hỏi nào mới. "
                + "Nếu anh/chị không còn gì bổ sung, bấm nút \"Write Requirement\" để mình tạo tài liệu nhé.",
            new List<string>());
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
    /// nó thấm vào Product Brief. Lượt USER (ghi chú + danh sách file đính kèm) được lưu NGAY khi có gì để
    /// lưu — bubble hiển thị ảnh trong hội thoại như ChatGPT/Claude, kể cả khi bước đọc phía sau lỗi.
    /// Lời gọi LLM lỗi / trả rỗng ⇒ lưu một lượt assistant ⚠️ (cùng tiền tố với lượt chat thường) để UI
    /// tô đỏ + hiện nút "Thử lại" thay vì im lặng khiến người dùng tưởng BA không trả lời.
    /// Fail-open với upload: chưa cấu hình BA / lỗi bất ngờ ⇒ trả false, upload vẫn thành công như cũ.
    /// </summary>
    public async Task<bool> AcknowledgeSourcesAsync(Guid projectId, string? note = null, IReadOnlyList<ChatAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        // Lượt user (ghi chú/ảnh) được ghi bên trong try; giữ lại id BA + cờ đã-ghi ở ngoài để nhánh
        // catch còn ĐÓNG được lượt bằng một lượt ⚠️ thay vì để hội thoại cụt ở lượt user (xem
        // RunTurnGuaranteedAsync — cùng một cái bẫy "màn hình treo ở BA đang soạn…").
        Guid? baId = null;
        var userTurnAppended = false;
        try
        {
            var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
            if (project == null)
                return false;

            var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
            if (ba == null)
                return false;
            var model = ba.AiModel!;
            baId = ba.Id;

            // Lưu lượt user TRƯỚC các bước có thể lỗi: ghi chú (nếu có) + danh sách file vừa đính kèm để
            // bubble render ảnh ngay trong hội thoại. Không ghi chú, không file ⇒ không thêm lượt nào.
            var trimmedNote = note?.Trim();
            var attachmentsJson = attachments is { Count: > 0 } ? JsonSerializer.Serialize(attachments) : null;
            if (!string.IsNullOrEmpty(trimmedNote) || attachmentsJson != null)
            {
                await _conversationLog.AppendAsync(projectId, ba.Id, "user", trimmedNote ?? string.Empty, attachmentsJson: attachmentsJson, cancellationToken: cancellationToken);
                userTurnAppended = true;
            }

            var sources = await _db.ProjectSourceFiles
                .AsNoTracking()
                .Where(s => s.ProjectId == projectId)
                .OrderBy(s => s.CreatedAt)
                .ToListAsync(cancellationToken);
            var sourceContents = _sourceContextBuilder.Build(sources, model.SupportsVision);
            if (sourceContents.Count == 0)
            {
                // Không có gì đọc được (model không vision với ảnh / PDF scan). Nếu người dùng vừa gửi từ
                // khung chat thì nói rõ lý do thay vì im lặng — họ còn đường khác (gõ tóm tắt nội dung ảnh).
                if (!string.IsNullOrEmpty(trimmedNote) || attachmentsJson != null)
                    await _conversationLog.AppendAsync(projectId, ba.Id, "assistant",
                        "Mình đã nhận được file anh/chị gửi, nhưng model AI hiện tại chưa đọc được nội dung bên trong "
                        + "(ảnh cần model hỗ trợ vision; PDF dạng scan không bóc được chữ). "
                        + "Anh/chị có thể gõ tóm tắt các thông tin chính trong file vào chat để mình nắm nhé.",
                        cancellationToken: cancellationToken);
                return false;
            }

            // Ghi chú người dùng gõ cạnh ảnh (nếu có) → BA đọc đúng trọng tâm thay vì tóm tắt chung chung.
            var promptText = string.IsNullOrEmpty(trimmedNote)
                ? "Đây là các tài liệu nguồn tôi vừa đính kèm. Bạn đọc kỹ và kể lại cụ thể những gì rút được từ chúng để tôi xác nhận nhé."
                : $"Đây là các tài liệu nguồn tôi vừa đính kèm, kèm ghi chú của tôi: \"{trimmedNote}\". Bạn đọc kỹ và kể lại cụ thể những gì rút được từ chúng để tôi xác nhận nhé.";

            var userContent = new List<AIContent> { new TextContent(promptText) };
            userContent.AddRange(sourceContents.Contents);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/source-ack.v2.md")),
                new(ChatRole.User, userContent)
            };

            // Lời gọi LLM có thể THROW trước cả khi tới model (ví dụ ApiKey rỗng làm BuildClient ném
            // ArgumentException) chứ không chỉ trả IsSuccess=false — bắt riêng tại đây để lỗi nào cũng
            // thành lượt ⚠️ hiển thị được, thay vì lọt xuống catch-all và BA "mất tích" không dấu vết.
            LlmCallResult? callResult = null;
            BASourceAckReply? parsed = null;
            string? callError = null;
            try
            {
                (callResult, parsed) = await _llm.ChatStructuredAsync<BASourceAckReply>(
                    model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BASourceAck"),
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Bóc lớp bọc trước khi hiện: SDK gói lỗi thật xuống dưới vài tầng và câu ngoài cùng
                // ("Retry failed after 4 tries…") không nói được gì. Xem LlmFailureDescriber.
                callError = LlmFailureDescriber.Unwrap(ex).Message;
            }

            var reply = callResult is { IsSuccess: true } ? (parsed ?? _replyParser.Parse(callResult.Content)) : null;
            if (reply == null || string.IsNullOrWhiteSpace(reply.Message))
            {
                // Trước đây chỗ này return false im lặng: ghi chú của user đã hiện trong hội thoại nhưng BA
                // "mất tích". Lưu lượt ⚠️ như RunTurnAsync để UI tô đỏ + nút "Thử lại" (retry xóa lượt lỗi
                // rồi chạy lại lượt chat thường — tài liệu nguồn vẫn được đính vào lượt user mới nhất).
                var detail = callError ?? callResult?.ErrorMessage ?? callResult?.Content;
                await _conversationLog.AppendAsync(projectId, ba.Id, "assistant",
                    $"{ConversationTranscriptBuilder.LlmFailurePrefix}, chưa thể đọc và tóm tắt tài liệu vừa gửi. Chi tiết: {detail}",
                    cancellationToken: cancellationToken);
                return false;
            }

            var suggestionsJson = reply.Suggestions.Count > 0 ? JsonSerializer.Serialize(reply.Suggestions) : null;
            await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", reply.Message.Trim(), suggestionsJson, reply.MultiSelect, cancellationToken: cancellationToken);

            // Đây là lượt DUY NHẤT model nhìn thấy ảnh. Cất phần nó ghi lại được về từng hình để các lượt
            // chat sau dùng chữ thay ảnh. Structured output tắt ⇒ parsed null ⇒ thử đọc lại từ raw content
            // (model vẫn hay trả đúng JSON dù không được ép); vẫn không có ⇒ không cất gì, ảnh tiếp tục đi
            // kèm như trước — tốn token nhưng không mất nội dung, đó mới là thứ không được phép hỏng.
            var notes = parsed?.SourceNotes
                ?? LlmJson.TryDeserialize<BASourceAckReply>(callResult?.Content, requireKnownProperty: true)?.SourceNotes;
            // Ảnh có thể bị GỠ ở phút chót rồi gọi lại (endpoint không nhận ảnh, hoặc request quá lớn — xem
            // EndpointQuirks.ShouldRetryWithoutImages). Lượt thành công đó model KHÔNG hề nhìn thấy hình nào,
            // nên tuyệt đối không được khóa VisionSummary bằng nó: khóa nhầm là mất vĩnh viễn đường nhìn lại
            // ảnh, và mọi lượt sau đọc một mô tả bịa.
            var imagesActuallySent = callResult is { RequestImageCount: > 0 };
            await StoreVisionSummariesAsync(
                imagesActuallySent ? sourceContents.FullyAttachedSourceIds : Array.Empty<Guid>(),
                sources, notes, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (userTurnAppended && baId.HasValue)
                await TryCloseTurnWithFailureAsync(projectId, baId.Value, new OperationCanceledException("Request đã bị hủy giữa chừng."));
            throw;
        }
        catch (Exception ex)
        {
            // Bước phụ trợ — lỗi thì bỏ qua, không làm hỏng upload. Nhưng nếu lượt user đã được ghi thì
            // phải đóng lượt lại, nếu không hội thoại kẹt vĩnh viễn ở "BA đang soạn câu trả lời…".
            if (userTurnAppended && baId.HasValue)
                await TryCloseTurnWithFailureAsync(projectId, baId.Value, ex);
            return false;
        }
    }

    /// <summary>
    /// Cất phần BA ghi lại được về nội dung các HÌNH vào <see cref="ProjectSourceFile.VisionSummary"/>, để từ
    /// lượt sau ảnh không phải upload lại nữa. Chỉ ghi cho các nguồn có TOÀN BỘ ảnh thực sự đã đi kèm lượt
    /// vừa rồi (<paramref name="fullyAttachedIds"/>): mô tả dựa trên nửa số hình rồi khóa lại là mất trắng
    /// nửa còn lại, thà tốn thêm một lượt ảnh.
    ///
    /// Ghép ghi chú về nguồn theo TÊN FILE — model chép lại tên từ dòng "[Nguồn: ...]" nên khớp thẳng được;
    /// nguồn không có ghi chú tương ứng thì để nguyên (ảnh vẫn đi kèm lượt sau) chứ KHÔNG lấy tạm ghi chú
    /// của file khác hay lấy phần message tóm tắt: một mô tả sai chỗ còn tệ hơn không có, vì nó khóa luôn
    /// đường nhìn lại ảnh.
    /// </summary>
    private async Task StoreVisionSummariesAsync(
        IReadOnlyList<Guid> fullyAttachedIds, List<ProjectSourceFile> sources,
        List<SourceVisionNote>? notes, CancellationToken cancellationToken)
    {
        if (fullyAttachedIds.Count == 0 || notes is not { Count: > 0 })
            return;

        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            var name = note.FileName?.Trim();
            var text = note.Note?.Trim();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(text))
                byName[name] = text;
        }

        var updated = 0;
        foreach (var id in fullyAttachedIds)
        {
            var source = sources.FirstOrDefault(s => s.Id == id);
            if (source == null || !byName.TryGetValue(source.FileName.Trim(), out var summary))
                continue;

            // sources đọc bằng AsNoTracking ⇒ cập nhật qua entity đang track, không attach bản no-tracking
            // (sẽ đụng cả ExtractedText dài).
            var tracked = await _db.ProjectSourceFiles.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (tracked == null || !string.IsNullOrWhiteSpace(tracked.VisionSummary))
                continue;

            tracked.VisionSummary = summary;
            updated++;
        }

        if (updated > 0)
            await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Kết quả 3 bước chuẩn bị ngữ cảnh của một lượt chat.</summary>
    private async Task<(ConversationMemoryService.Memory Memory, string? UserMemory, RequirementCoverageService.CoverageUpdate Coverage)> PrepareTurnContextAsync(
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
        // Echo cả sơ đồ luồng đã vẽ (nếu có): BA các lượt sau thấy mình ĐÃ trình bày luồng nào cho người
        // dùng xác nhận — sửa đúng bước bị người dùng đính chính thay vì vẽ lại từ đầu một luồng khác.
        var flowDiagram = ConversationTurnRenderer.ParseFlowDiagram(c.FlowDiagram)
            .Select(s => new { actor = s.Actor, action = s.Action, outcome = s.Outcome });
        // Echo cả các câu hỏi của lượt GỘP: đây là chỗ model học rằng gộp là hợp lệ VÀ học nhịp gộp của
        // chính nó. Bỏ trường này thì mọi lượt cũ trông như lượt một-câu và model trượt về một-câu-một-lượt
        // sau vài vòng — đúng kiểu trượt format mà hàm này sinh ra để chặn.
        var questions = ConversationTurnRenderer.ParseQuestions(c.Questions)
            .Select(q => new { group = q.Group, question = q.Question, suggestions = q.Suggestions, multiSelect = q.MultiSelect });
        return JsonSerializer.Serialize(new { message = c.Message, suggestions, multiSelect = c.SuggestionsMultiSelect, questions, ready, flowDiagram });
    }
}
