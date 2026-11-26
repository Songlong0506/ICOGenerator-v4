using ICOGenerator.Localization;
using System.Text.Encodings.Web;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

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
    private readonly ReportMapDraftService _reportMapDraft;
    private readonly ChecklistNoteStore _checklistNotes;
    private readonly InterviewJudge? _judge;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly string _productBriefFileName;
    private readonly BAChatTurnTracker? _turnTracker;

    /// <summary>
    /// Ngưỡng ân hạn trước khi một lượt đang chờ bị coi là ĐÃ CHẾT khi không có lượt nào đang chạy trong
    /// tiến trình (xem <see cref="BAChatTurnTracker"/>). Chỉ dùng cho các lượt KHÔNG được sổ theo dõi ghi
    /// nhận — tiến trình vừa khởi động lại, hoặc lượt do instance khác chạy — nên đủ rộng để không cắt
    /// ngang một lượt thật, mà vẫn ngắn hơn nhiều so với việc treo màn hình vĩnh viễn.
    /// </summary>
    public static readonly TimeSpan ReplyStaleAfter = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Hai chip dự phòng của NHỊP TÓM TẮT KIỂM CHỨNG — bộ mà <c>requirement-chat.v4.md</c> kê sẵn cho lượt
    /// này. Lượt tóm tắt là câu ĐÓNG (gật, hoặc đòi sửa), nên nó phải có nút để bấm; thiếu nút thì model
    /// tự trượt sang hỏi độ ĐẦY ĐỦ của cả buổi phỏng vấn và nhận về một lời tuyên bố hoàn tất mà bản đồ
    /// bao phủ không hề công nhận (xem chốt chặn ở <see cref="ChatAsync"/>).
    /// </summary>
    public static IReadOnlyList<string> SummaryCheckSuggestions =>
        new[] { Loc.T("BAChatService.SummaryCheck.Confirm"), Loc.T("BAChatService.SummaryCheck.Revise") };

    /// <summary>
    /// Câu dẫn dự phòng cho lượt bày bảng luồng, dùng khi model không viết được câu dẫn
    /// dùng được. Nó phải CHỈ VÀO BẢNG chứ không kết bằng một câu hỏi đóng: lượt có bảng không có chip, nên
    /// một câu hỏi ở đây là câu hỏi KHÔNG CÓ NÚT TRẢ LỜI — người dùng đi tìm nút "Đúng rồi" không thấy trong
    /// khi việc thật sự phải làm nằm ngay dưới.
    /// </summary>
    public static string FlowMapIntro => Loc.T("BAChatService.FlowMapIntro");

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
        ReportMapDraftService reportMapDraft,
        ChecklistNoteStore checklistNotes,
        IServiceScopeFactory? scopeFactory = null,
        BAChatTurnTracker? turnTracker = null,
        InterviewJudge? judge = null,
        IProjectArtifactCatalog? artifactCatalog = null)
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
        _reportMapDraft = reportMapDraft;
        _checklistNotes = checklistNotes;
        // null (test/không có DI đầy đủ) ⇒ các bước chuẩn bị chạy TUẦN TỰ trên chính scope này —
        // hành vi cũ. Có factory ⇒ chạy SONG SONG, mỗi bước một scope riêng (xem PrepareTurnContextAsync).
        _scopeFactory = scopeFactory;
        // null (test) ⇒ không có sổ theo dõi lượt đang chạy: GetReplyStateAsync chỉ xét tuổi lượt user.
        _turnTracker = turnTracker;
        // null (test không cần phán quyết về nghĩa) ⇒ không có lượt trọng tài: các chốt chặn chỉ còn phần
        // tất định (khớp nguyên văn), đúng hành vi của một lời gọi trọng tài bị lỗi — xem InterviewJudge.
        _judge = judge;
        // Tên file Product Brief để tra bản ĐÃ DUYỆT (chế độ yêu cầu thay đổi — RequirementChangeMode).
        _productBriefFileName = (artifactCatalog ?? new ProjectArtifactCatalog()).ProductBrief.FileName;
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
        var message = Loc.T("BAChatService.TryCloseTurnWithFailureAsync", RequirementScreenText.LlmFailurePrefix, ex.Message);
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
            || !(lastTurn.Message ?? string.Empty).StartsWith(RequirementScreenText.LlmFailurePrefix, StringComparison.Ordinal))
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
    public Task<BAChatTurnResult> EditLastUserTurnAsync(Guid projectId, string newMessage, Action<string>? onStatus = null, Action<string>? onToken = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newMessage))
            return Task.FromResult(new BAChatTurnResult { Status = ChatWithBAResult.NothingToRetry });

        return RerunLastUserTurnAsync(projectId, newMessage, onStatus, onToken, cancellationToken);
    }

    /// <summary>
    /// SOẠN LẠI câu trả lời cho lượt user cuối — nút "↻ thử lại" ngay trên bong bóng của người dùng: xóa
    /// câu trả lời cũ rồi chạy lại lượt trên CHÍNH câu hỏi đó. Cùng lõi với
    /// <see cref="EditLastUserTurnAsync"/>, khác đúng một điều: câu hỏi không đổi.
    /// <para>
    /// Vì sao nó không phải là <see cref="RetryLastTurnAsync"/>: đường kia chỉ ĐÓNG một lượt đã hỏng (lượt
    /// ⚠️ hoặc lượt user còn cụt) và từ chối khi hội thoại đang lành lặn — nó không được phép xóa một câu
    /// trả lời đã có, vì cú bấm sinh ra nó là cú bấm trên một trang có thể đã cũ. Đường này thì ngược lại:
    /// người dùng nhìn thấy câu trả lời và nói rõ "trả lời lại đi", nên xóa câu trả lời cũ chính là điều
    /// họ vừa yêu cầu.
    /// </para>
    /// <para>
    /// Không có nó, muốn một câu trả lời khác cho cùng câu hỏi thì chỉ còn hai đường đều sai: bấm "✎ sửa"
    /// rồi lưu lại y nguyên (nút đó cố ý KHÔNG chạy lượt nào khi nội dung không đổi), hoặc gõ thêm một câu
    /// "trả lời lại giúp mình" — mà câu đó thành một lượt user THẬT trong hội thoại và đi thẳng vào bản đồ
    /// bao phủ lẫn bộ nhớ, tức trả giá bằng chính thứ nó không định nói.
    /// </para>
    /// </summary>
    public Task<BAChatTurnResult> RegenerateLastReplyAsync(Guid projectId, Action<string>? onStatus = null, Action<string>? onToken = null, CancellationToken cancellationToken = default) =>
        RerunLastUserTurnAsync(projectId, null, onStatus, onToken, cancellationToken);

    /// <summary>
    /// Lõi dùng chung của "✎ sửa" và "↻ thử lại": xóa câu trả lời của lượt user CUỐI (nếu đã có), ghi đè
    /// nội dung lượt đó khi <paramref name="newMessage"/> khác null, kéo lùi các con trỏ gộp rồi chạy lại
    /// lượt. <paramref name="newMessage"/> = null nghĩa là giữ nguyên câu hỏi (đường soạn lại).
    /// </summary>
    private async Task<BAChatTurnResult> RerunLastUserTurnAsync(Guid projectId, string? newMessage, Action<string>? onStatus, Action<string>? onToken, CancellationToken cancellationToken)
    {
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

        // Lượt cuối là assistant ⇒ xóa nó (câu trả lời cho câu hỏi cũ / câu trả lời sắp được soạn lại),
        // lượt user cần chạy lại nằm ngay trước. Lượt cuối là user ⇒ câu trả lời chưa/không bao giờ tới,
        // chạy thẳng lượt đó (không xóa gì).
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

        // Đường SOẠN LẠI không đụng vào câu hỏi (newMessage = null) — nó chỉ xin một câu trả lời khác.
        if (newMessage != null)
        {
            userTurn.Message = newMessage.Trim();
        }
        // Câu trả lời cũ (thứ đã đóng dấu "ngoài phạm vi" lên lượt này) vừa bị xoá ⇒ dấu đó hết căn cứ. Lượt
        // chạy lại đóng dấu lại theo câu trả lời mới (SaveTurnAsync); hạ ở đây để lượt chạy lại mà vỡ giữa
        // chừng không để lại một lượt user mang dấu của câu trả lời đã xoá.
        userTurn.OffTopic = false;
        await _db.SaveChangesAsync(cancellationToken);

        // Kéo lùi mọi con trỏ gộp về TRƯỚC lượt user vừa chạy lại. Hai lý do, và cả hai đều đúng cho cả
        // hai đường: (1) bản sửa — các bản đúc kết phải dựng lại từ nội dung MỚI; (2) xóa một lượt làm SỐ
        // LƯỢT giảm đi, nên con trỏ "đã gộp tới lượt thứ n" có thể vượt quá số lượt hiện có — lúc đó mọi
        // lượt gộp sau này thấy delta rỗng và bản đồ/nhật ký đóng băng vĩnh viễn.
        var turnCount = await _db.AgentConversations.CountAsync(c => c.ProjectId == projectId, cancellationToken);
        var beforeLastUserTurn = Math.Max(0, turnCount - 1);
        // BẢN ĐỒ BAO PHỦ không chỉ lùi con trỏ mà lùi cả NỘI DUNG: câu gốc đã được gộp vào bản đồ ở đầu
        // lượt gốc, và bản đồ gộp lũy tiến — chỉ kéo con trỏ thì lượt chạy lại chắt câu đã sửa CHỒNG LÊN
        // một bản còn chứa ý cũ, và ý đó ở lại mãi nếu câu mới không trực tiếp phủ định nó. Không có ảnh
        // chụp dùng được (dự án cũ, hoặc một lượt gộp khác đã chen vào) ⇒ lùi về hành vi cũ: chỉ kéo con
        // trỏ. Xem CoverageUndoSnapshot.
        if (!CoverageUndoSnapshot.TryRestore(project, beforeLastUserTurn))
            project.CoverageHarvestedTurnCount = Math.Min(project.CoverageHarvestedTurnCount, beforeLastUserTurn);
        project.UxDesignedTurnCount = Math.Min(project.UxDesignedTurnCount, beforeLastUserTurn);
        project.UserMemoryHarvestedTurnCount = Math.Min(project.UserMemoryHarvestedTurnCount, beforeLastUserTurn);
        project.SummarizedTurnCount = Math.Min(project.SummarizedTurnCount, beforeLastUserTurn);
        await _db.SaveChangesAsync(cancellationToken);

        return await RunTurnGuaranteedAsync(project, ba, ba.AiModel!, onStatus, onToken, cancellationToken);
    }

    /// <summary>
    /// Ngữ cảnh TẤT ĐỊNH của một lượt chat: mọi thứ suy được từ dữ liệu dự án TRƯỚC khi gọi model, chốt
    /// đúng MỘT LẦN ở đầu lượt.
    ///
    /// <para>
    /// Vì sao nó là một vật chứ không phải mười lăm biến cục bộ: cả khối lắp ngữ cảnh (<see
    /// cref="BuildMessagesAsync"/>) lẫn các chốt chặn chạy SAU lời gọi model đều đọc đúng bộ dữ kiện này —
    /// cổng nào đang mở, bảng nào vừa được BA soạn ngầm. Tính lại ở nhánh thứ hai là mở đường cho hai nhánh xét trên hai bản khác nhau, đúng loại vênh mà cả tầng cổng tất định của
    /// luồng này sinh ra để dẹp.
    /// </para>
    /// </summary>
    private sealed record TurnContext(
        Project Project,
        ConversationMemoryService.Memory Memory,
        string? UserMemory,
        RequirementCoverageService.CoverageUpdate CoverageUpdate,
        List<ProjectSourceFile> Sources,
        SourceContext SourceContents,
        bool SourceTextInPrefix,
        int LastUserIndex,
        List<AskedQuestionHistory.AskedTurn> AskedBefore,
        InterviewTableKind Table,
        int UserTurnCount,
        ProjectDocumentLookup.ApprovedDocument? ApprovedBrief,
        bool ChangeMode)
    {
        public Guid ProjectId => Project.Id;

        public List<AgentConversation> Recent => Memory.RecentTurns;
    }

    // Lõi một lượt trả lời của BA. Tách khỏi ChatAsync để đường "thử lại lượt lỗi" chạy lại y hệt mà
    // không ghi thêm lượt user.
    //
    // Bốn chặng, đọc từ trên xuống: chốt ngữ cảnh tất định của lượt → lắp prompt → gọi model → nắn hình
    // dạng lượt trả lời rồi lưu.
    private async Task<BAChatTurnResult> RunTurnAsync(Project project, Agent ba, AiModel model, Action<string>? onStatus, Action<string>? onToken, CancellationToken cancellationToken)
    {
        // Các bước chuẩn bị dưới đây có thể gọi LLM (tóm tắt/bồi hồ sơ/bản đồ bao phủ) — báo trạng thái
        // để người dùng thấy BA "đang làm việc" thay vì spinner câm khi stream.
        onStatus?.Invoke(Loc.T("BAChatService.RunTurnAsync"));
        var turn = await BuildTurnContextAsync(project, ba, model, cancellationToken);

        var messages = await BuildMessagesAsync(turn, ba, cancellationToken);

        var tableContract = InterviewTableContract.For(turn.Table);
        onStatus?.Invoke(tableContract == null
            ? Loc.T("BAChatService.RunTurnAsync.2")
            : Loc.T("BAChatService.RunTurnAsync.3", tableContract.Label));

        // Bảng của lượt được dựng NGAY TRONG vòng gọi model (xem CallForTurnAsync): "model có trả bảng
        // không" là điều kiện dừng của vòng đó, nên nó phải được trả lời trước khi lượt đi tiếp — và dựng
        // một lần rồi dùng lại thì bước nắn lượt phía dưới không phải dựng lại y hệt lần thứ hai.
        var draft = new BAChatTurnDraft();
        var (callResult, parsedReply) = await CallForTurnAsync(draft, turn, messages, ba, model, onStatus, onToken, cancellationToken);

        if (!callResult.IsSuccess || parsedReply == null)
        {
            // Lỗi gọi model thành một lượt assistant CÓ NHÃN thay vì một HTTP 500 — nhưng không bao giờ
            // bày một lỗi API ra như thể đó là câu trả lời bình thường của BA. Tiền tố dùng chung với
            // ConversationTranscriptBuilder để transcript tổng hợp yêu cầu lọc được các lượt lỗi này ra.
            draft.Reply = Loc.T("BAChatService.RunTurnAsync.4", RequirementScreenText.LlmFailurePrefix, callResult.ErrorMessage ?? callResult.Content);
        }
        else if (parsedReply.OffTopic)
        {
            // Câu NGOÀI PHẠM VI: lượt do cơ chế dựng trọn (câu từ chối cố định + chép lại câu hỏi đang dở),
            // và KHÔNG chốt chặn nào của ShapeTurnAsync chạy trên nó. Chúng đều xét một lượt HỎI của buổi
            // phỏng vấn — chạy ở đây thì phanh chống hỏi lại thấy câu hỏi vừa chép lại là "câu đã hỏi" và
            // thay nó đi, còn chốt chặn lượt câm thì đổi lời từ chối thành lời mời bấm nút (đúng ca sinh ra
            // nhánh này). Không gọi lượt trọng tài cũng là bớt một lời gọi LLM cho một câu không có gì để phán.
            OffTopicExchange.Shape(draft, OffTopicExchange.PendingAsk(turn.Recent.Take(Math.Max(0, turn.LastUserIndex))));
        }
        else
        {
            await ShapeTurnAsync(draft, parsedReply, turn, ba, model, cancellationToken);
        }

        return await SaveTurnAsync(turn, ba, draft, cancellationToken);
    }

    /// <summary>
    /// Số lần gọi model TỐI ĐA cho một lượt bày bảng: một lần đầu cộng tối đa ba lần đòi lại. Lượt chat
    /// thường không đếm — nó chỉ gọi đúng một lần như trước.
    ///
    /// <para>
    /// Vì sao phải đòi lại chứ không chấp nhận lượt đầu: lượt bày bảng bị prompt ép vào một hình dạng —
    /// <c>message</c> một câu, không chip, không thẻ hỏi, không kết bằng dấu hỏi — TRÙNG KHÍT với hình dạng
    /// LƯỢT CÂM (<see cref="BAChatTurnDraft.IsSilent"/>). Nên khi model viết đúng câu dẫn mà quên trường
    /// bảng, lượt ấy không "chạy như một lượt chat thường" như fail-open dự tính: nó rơi thẳng vào chốt
    /// chặn lượt câm và bị thay bằng một câu hỏi của cổng bao phủ, lạc hẳn khỏi việc BA vừa hứa. Ca thật
    /// (2026-09-08, một lượt bày bảng): model trả 168 token gồm mỗi câu *"…bấm nút Gửi bảng … để xác nhận
    /// giúp mình ạ"* và không có trường bảng; người dùng nhận được một câu hỏi về nhóm «Thông báo / nhắc
    /// nhở», còn câu của model thì không bao giờ được lưu.
    /// </para>
    ///
    /// <para>
    /// Ba lần là trần CÓ CHỦ ĐÍCH, không phải con số cho đẹp: mỗi lần đòi lại cộng thêm một lời gọi vào độ
    /// chờ của lượt (ca thật: ~3,6 giây mỗi lời gọi), nên trần cao hơn đổi một lượt hỏng lấy một lượt treo.
    /// Hết trần thì lượt vẫn fail-open như cũ — cổng bảng đọc bản đồ bao phủ chứ không đọc lịch sử lượt
    /// (<see cref="InterviewTableGate.Select(Project)"/>), nên nó mở lại ở lượt kế và bảng được bày lại.
    /// </para>
    /// </summary>
    public const int MaxTableAttempts = 4;

    /// <summary>
    /// Gọi model cho một lượt — và với lượt BÀY BẢNG thì gọi lại tới khi model chịu trả bảng, tối đa
    /// <see cref="MaxTableAttempts"/> lần. Trả về lời gọi CUỐI CÙNG cùng bản đã chuẩn hoá của nó;
    /// <paramref name="draft"/> đi ra mang sẵn các dòng bảng dựng được (rỗng khi model không chịu trả).
    ///
    /// <para>
    /// <b>Điều kiện dừng là BẢNG DỰNG ĐƯỢC, không phải trường JSON có mặt.</b> Một trường
    /// <c>reportMap: []</c>, hay một bảng mà mọi dòng đều bị builder loại (dòng trỏ vào màn hình ngoài
    /// phạm vi, dòng rỗng ruột), hỏng đúng bằng việc thiếu hẳn trường — và chỉ có builder mới biết. Nên
    /// vòng này dựng bảng thật bằng <see cref="FillTableRows"/> rồi hỏi
    /// <see cref="BAChatTurnDraft.CarriesTable"/>, thay vì soi JSON thô.
    /// </para>
    ///
    /// <para>
    /// <b>Lượt bày bảng KHÔNG stream.</b> Frame token chỉ biết CỘNG chữ vào bong bóng đang gõ (xem
    /// <c>handleFrame</c> trong <c>requirements.js</c>) nên một lượt có thể phải gọi lại vài lần thì không
    /// stream được: bản nháp của lần hỏng không rút lại được, và lần sau sẽ nối tiếp vào nó. Đổi lại không
    /// mất gì mấy — phần hiển thị được của lượt này chỉ là MỘT câu dẫn, còn thân bài là JSON bảng vốn đã bị
    /// <see cref="BAChatTokenFilter"/> lọc khỏi luồng — và người dùng nhận dòng trạng thái gọi đúng tên
    /// việc đang chạy thay cho một câu dẫn nhấp nháy rồi biến mất.
    /// </para>
    /// </summary>
    private async Task<(LlmCallResult Result, BAChatReply? Parsed)> CallForTurnAsync(
        BAChatTurnDraft draft, TurnContext turn, List<ChatMessage> messages, Agent ba, AiModel model,
        Action<string>? onStatus, Action<string>? onToken, CancellationToken cancellationToken)
    {
        // Lượt chat thường thoát ngay ở vòng đầu (nhánh `contract == null` bên dưới), nên trần này chỉ
        // thật sự đếm cho lượt bày bảng.
        var contract = InterviewTableContract.For(turn.Table);

        for (var attempt = 1; ; attempt++)
        {
            // BA được nhắc trả JSON {message, suggestions}: dùng structured output khi model được bật, ngược
            // lại parser luôn fallback an toàn về text thuần. Khi có onToken, luồng token thô (cú pháp JSON)
            // được lọc qua BAChatTokenFilter để chỉ phần message hiển thị được stream lên UI — ở mọi mức
            // structured output, vì mức nào cũng đi đường stream. UI vẫn nhận bản chốt ở done.
            var tokenFilter = onToken == null || contract != null ? null : new BAChatTokenFilter(onToken);
            var (callResult, structuredReply) = await _llm.ChatStructuredAsync<BAChatReply>(
                model, messages, new ModelCallLogContext(turn.ProjectId, ba, "BAChat"),
                tokenFilter == null ? null : tokenFilter.Feed, cancellationToken);

            if (!callResult.IsSuccess)
                return (callResult, null);

            // Đường structured output trả thẳng BAChatReply (không qua Parse), nên phải chuẩn hoá RIÊNG:
            // trần "tối đa 4 câu hỏi một lượt" và việc hạ lượt-gộp-một-câu về đường một-câu sống trong
            // Normalize. Bỏ bước này thì các model tốt (đường mặc định) là các model KHÔNG bị chặn.
            var parsedReply = structuredReply != null
                ? _replyParser.Normalize(structuredReply)
                : _replyParser.Parse(callResult.Content);

            // Câu NGOÀI PHẠM VI cũng thoát ngay, kể cả ở lượt bày bảng: đòi lại bảng ở đây là đốt thêm tới ba
            // lời gọi để ép model bày bảng làm câu trả lời cho một câu hỏi giá vàng. Bảng không mất gì — cổng
            // của nó đọc bản đồ bao phủ, nên nó mở lại ở lượt kế.
            if (contract == null || parsedReply.OffTopic)
                return (callResult, parsedReply);

            // Dựng đè lên chính draft của lượt: turn.Table cố định suốt lượt nên mỗi lần dựng ghi đè đúng
            // một danh sách, và lần dựng CUỐI là thứ bước nắn lượt đọc.
            FillTableRows(draft, parsedReply, turn);
            if (draft.CarriesTable || attempt >= MaxTableAttempts)
                return (callResult, parsedReply);

            onStatus?.Invoke(Loc.T("BAChatService.CallForTurnAsync", contract.Label, attempt + 1, MaxTableAttempts));

            // ĐÒI LẠI bằng cách NỐI THÊM vào cuối, không dựng lại prompt: tiền tố dài (ca thật 68k token,
            // phần lớn đọc từ cache của endpoint) giữ nguyên thì lần gọi lại rẻ hơn hẳn một lượt mới. Lượt
            // hỏng được chép lại đúng vai assistant rồi mới tới lời đòi — model đọc lại chính thứ mình vừa
            // trả và biết phải sửa gì, thay vì nhận một mệnh lệnh treo lơ lửng.
            //
            // Lượt rỗng thì KHÔNG chép: một message assistant rỗng bị nhiều endpoint từ chối thẳng, và ở đây
            // nó cũng chẳng cho model đọc lại được gì. Lời đòi đứng một mình vẫn đủ nghĩa.
            if (!string.IsNullOrWhiteSpace(callResult.Content))
                messages.Add(new ChatMessage(ChatRole.Assistant, callResult.Content));

            messages.Add(new ChatMessage(ChatRole.User, contract.RetryDemand));
        }
    }

    /// <summary>
    /// Chốt mọi dữ kiện TẤT ĐỊNH của lượt: ba bước chuẩn bị chạy song song, tài liệu nguồn, và cổng nào
    /// đang mở. Xem <see cref="TurnContext"/> về việc vì sao chúng phải được chốt một lần.
    /// </summary>
    private async Task<TurnContext> BuildTurnContextAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        // Ba bước chuẩn bị (bộ nhớ hội thoại + hồ sơ user + bản đồ bao phủ) độc lập với nhau và là phần
        // chậm nhất trước khi BA "đặt bút" — chạy SONG SONG để độ chờ mỗi lượt bằng bước chậm nhất thay vì
        // tổng ba bước. Xem PrepareTurnContextAsync về cách cô lập DbContext.
        //
        // Đã có Brief ĐÃ DUYỆT ⇒ phần hội thoại trước mốc duyệt được GỘP hẳn vào tóm tắt (một lần, lượt đầu
        // tiên sau mốc), không gửi nguyên văn nữa: bản đã duyệt chở nó. Xem RequirementChangeMode.
        var approvedBrief = await LoadApprovedBriefAsync(project.Id, cancellationToken);
        var hasApprovedBaseline = RequirementChangeMode.HasApprovedBaseline(approvedBrief, project.BriefApprovedTurnCount);
        var (memory, userMemory, coverageUpdate) = await PrepareTurnContextAsync(
            project, ba, model, hasApprovedBaseline ? project.BriefApprovedTurnCount : 0, cancellationToken);
        var recent = memory.RecentTurns;

        // Ba nhánh (khi chạy song song) ghi cột bộ nhớ qua context riêng — đồng bộ lại giá trị bản đồ lên
        // entity đang track để các chỗ đọc phía dưới (khối ngữ cảnh, các cổng, kết quả trả về) thấy bản tươi.
        project.RequirementCoverageMap = coverageUpdate.Map;

        // Tài liệu nguồn (ảnh/PDF) của project: gắn vào ĐÚNG lượt user mới nhất (một lần) để BA "thấy" khi trả lời,
        // thay vì lặp lại ở mọi message trong lịch sử. Model không vision ⇒ builder chỉ trả phần text bóc từ PDF.
        // Lưu ý chi phí: mỗi lượt chat là một request MỚI, nên nguồn nào còn phải đi bằng ẢNH thì lượt nào
        // cũng upload lại ảnh đó. Thứ chặn việc này là VisionSummary — nguồn đã được BA mô tả nội dung hình
        // thành chữ ở lượt xác nhận tài liệu thì từ đây chỉ còn mang phần chữ (xem SourceContextBuilder).
        // Chỉ đọc (builder không ghi gì lên entity) ⇒ AsNoTracking, khỏi track cả ExtractedText dài.
        var sources = await _db.ProjectSourceFiles
            .AsNoTracking()
            .Where(s => s.ProjectId == project.Id)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        var lastUserIndex = recent.FindLastIndex(c => c.Role != "assistant");

        // Bảng báo cáo được RÁP NGẦM ngay trong buổi phỏng vấn (rẻ, một lời gọi, và tự im khi chưa tới lúc).
        // Phần THIẾT KẾ — bản thiết kế UX và bảng phân quyền dựng trên nó — KHÔNG chạy ở đây: chúng chạy một
        // lần ở bước "Write Requirement" (DesignPreparationService), khi phạm vi đã ngã ngũ. Thiết kế giữa buổi
        // là thiết kế trên một phạm vi còn trôi, rồi phải thiết kế lại mỗi khi nó trôi tiếp.
        await DraftReportMapAsync(project, ba, model, cancellationToken);

        // MỘT cổng chọn bảng bày ra (nay chỉ còn bảng luồng), TẤT ĐỊNH và mỗi lượt nhiều nhất MỘT bảng: hai
        // khối "## LƯỢT NÀY:" cùng lúc là hai mệnh lệnh chọi nhau. Xem InterviewTableGate.
        var table = InterviewTableGate.Select(project);
        var changeMode = RequirementChangeMode.IsActive(approvedBrief, project.BriefApprovedTurnCount, table);

        // Ở chế độ yêu cầu thay đổi chỉ gửi tài liệu nguồn người dùng tải lên SAU mốc duyệt: các file trước
        // mốc đã được đọc ở buổi phỏng vấn và nội dung của chúng đã chảy vào Brief đã duyệt (khối thống kê
        // cột của một file Excel lớn là ~20.000 ký tự mỗi lượt). File mới thì Brief chưa hề thấy.
        var sourceContents = _sourceContextBuilder.Build(
            changeMode ? await SourcesAfterApprovalAsync(project, sources, cancellationToken) : sources, model);

        return new TurnContext(
            project,
            memory,
            userMemory,
            coverageUpdate,
            sources,
            sourceContents,
            // TEXT TÀI LIỆU NGUỒN ĐI Ở ĐẦU PROMPT, KHÔNG PHẢI Ở LƯỢT USER CUỐI — xem BuildMessagesAsync.
            // Chỉ tách khi lượt này KHÔNG mang ảnh: còn ảnh thì chữ phải ở cạnh ảnh của chính nguồn đó.
            SourceTextInPrefix: sourceContents.Count > 0 && !sourceContents.HasImages,
            lastUserIndex,
            // Sổ "đã hỏi rồi": bản đồ bao phủ chỉ có độ phân giải theo NHÓM, nên một nhóm chưa [RÕ] dễ
            // khiến model phát lại nguyên văn câu hỏi mở đầu của chính nhóm ấy. Danh sách câu hỏi thật —
            // kèm câu người dùng đáp từng lượt — là thứ duy nhất phân biệt được "hỏi tiếp phần còn thiếu"
            // (kể cả câu người dùng đã bỏ qua) với "hỏi lại điều vừa được trả lời".
            AskedQuestionHistory.CollectTurns(recent),
            table,
            // SỐ LƯỢT người dùng đã trả lời, đếm trên CẢ hội thoại chứ không trên `recent`: cửa sổ nguyên
            // văn bị lược bớt khi hội thoại dài, tức đúng lúc con số này bắt đầu có nghĩa. Lượt người dùng
            // vừa gửi đã được lưu trước khi vào đây nên nó nằm trong số đếm. Xem InterviewPaceBudget.
            await _db.AgentConversations.CountAsync(
                c => c.ProjectId == project.Id && c.Role == "user", cancellationToken),
            approvedBrief,
            changeMode);
    }

    /// <summary>Bản Product Brief ĐÃ DUYỆT mới nhất của dự án; null khi chưa duyệt lần nào.</summary>
    private async Task<ProjectDocumentLookup.ApprovedDocument?> LoadApprovedBriefAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var approved = await _db.ProjectDocuments
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.VersionName != ProjectDocument.DraftVersion && d.FileName == _productBriefFileName)
            .ToListAsync(cancellationToken);
        return ProjectDocumentLookup.GetLatestApproved(approved, _productBriefFileName);
    }

    /// <summary>
    /// Các tài liệu nguồn tải lên SAU mốc duyệt Brief — mốc thời gian là lượt hội thoại cuối cùng nằm trong
    /// mốc (<c>Project.BriefApprovedTurnCount</c>), cùng thứ tự với các con trỏ bộ nhớ.
    /// </summary>
    private async Task<List<ProjectSourceFile>> SourcesAfterApprovalAsync(Project project, List<ProjectSourceFile> sources, CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
            return sources;

        var markTime = await _db.AgentConversations
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip(Math.Max(0, project.BriefApprovedTurnCount - 1))
            .Select(c => (DateTime?)c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return markTime == null ? sources : sources.Where(s => s.CreatedAt > markTime.Value).ToList();
    }

    /// <summary>
    /// Ráp bảng báo cáo ngay khi nhóm «Báo cáo / thống kê» vừa <c>[RÕ]</c> — xem
    /// <see cref="ReportMapDraftService"/>. Fail-open như lượt chắt lọc: lỗi thì lượt chat vẫn chạy, cổng còn
    /// mở nên lượt kế ráp lại (bước "Write Requirement" cũng ráp bù trước khi thiết kế).
    /// </summary>
    private async Task DraftReportMapAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        try
        {
            await _reportMapDraft.UpdateAsync(project, ba, model, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nuốt có chủ ý: lời gọi model lỗi đã có log ở ILlmClient, cổng còn mở nên lượt kế ráp lại.
        }
    }

    /// <summary>
    /// Lắp ngữ cảnh của lượt thành danh sách message gửi model. Nội dung từng khối nằm ở
    /// <see cref="BAChatPromptBlocks"/>; ở đây chỉ có THỨ TỰ và điều kiện đính — và thứ tự là một phần của
    /// hành vi, xem ghi chú prompt cache ngay dưới.
    /// </summary>
    private async Task<List<ChatMessage>> BuildMessagesAsync(TurnContext turn, Agent ba, CancellationToken cancellationToken)
    {
        if (turn.ChangeMode)
            return await BuildChangeRequestMessagesAsync(turn, cancellationToken);

        var project = turn.Project;
        // ĐIỂM CACHE (PromptCacheHint) đánh dấu chỗ kết thúc các đoạn prefix tĩnh: prompt nền, rồi hết các
        // khối "thỉnh thoảng mới đổi" (nguồn/tổ chức/checklist/hồ sơ), rồi bộ nhớ hội thoại. OpenAI không
        // cần dấu (tự cache theo prefix); Anthropic chỉ cache đúng ở chỗ được xin — xem AnthropicVertexChatClient.
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/requirement-chat.v4.md")).AsCacheBreakpoint()
        };

        // TEXT TÀI LIỆU NGUỒN ĐẶT NGAY ĐÂY, KHÔNG PHẢI Ở LƯỢT USER CUỐI — đây là một quyết định về CHI PHÍ,
        // không phải về cách trình bày. Prompt cache của OpenAI khớp theo PREFIX: mọi thứ đứng trước khối
        // đầu tiên thay đổi trong lượt này đều được phục vụ từ cache với giá rẻ hơn 10 lần. Text nguồn là
        // khối LỚN (tới 20.000 ký tự mỗi file) và TĨNH (không đổi cho tới khi người dùng upload thêm), mà
        // trước đây nó bị đính vào lượt user CUỐI CÙNG — vị trí biến động nhất trong cả danh sách — nên
        // lượt nào cũng trả giá đầy đủ cho đúng những byte không hề đổi.
        //
        // Không mất mát gì ở trạng thái ổn định — ảnh chỉ đi kèm cho tới khi BA ghi xong VisionSummary,
        // sau đó mọi lượt đều là lượt không ảnh (xem SourceContextBuilder).
        if (turn.SourceTextInPrefix)
            messages.Add(new ChatMessage(ChatRole.System, turn.SourceContents.TextOnly()));

        // Bối cảnh tổ chức Bosch render từ dữ liệu HR thật (OrgUnits/Associates, có cache) + đơn vị yêu cầu
        // của dự án (nếu đã gắn lúc tạo project): BA hiểu ngay tên phòng ban/chức danh người dùng nhắc tới,
        // gợi ý bằng tên phòng thật và hỏi luồng duyệt đúng ngôn ngữ manager/HoD. Fail-open: chưa có dữ
        // liệu ⇒ bỏ qua, chat như cũ. Xem OrganizationContextService.
        var organizationContext = await _orgContext.BuildCombinedContextAsync(project.OrgUnitCode, cancellationToken);
        if (!string.IsNullOrWhiteSpace(organizationContext))
            messages.Add(new ChatMessage(ChatRole.System, organizationContext));

        var checklistBucket = await _checklistNotes.ResolveBucketAsync(project.OrgUnitCode, cancellationToken);
        var learnedChecklist = await _checklistNotes.BuildForChatAsync(ba, checklistBucket, cancellationToken);
        if (!string.IsNullOrWhiteSpace(learnedChecklist))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.LearnedChecklist(learnedChecklist)));

        if (!string.IsNullOrWhiteSpace(turn.UserMemory))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.UserProfile(turn.UserMemory)));
        messages[^1].AsCacheBreakpoint();

        if (!string.IsNullOrWhiteSpace(turn.Memory.Summary))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.ConversationMemory(turn.Memory.Summary)).AsCacheBreakpoint());

        // Danh sách câu hỏi lấy từ CHÍNH lượt gộp vừa chạy, không đọc lại project.OpenQuestions: lượt gộp
        // có thể chạy trong một DI scope riêng (PrepareTurnContextAsync), nên entity ở đây chưa thấy nó.
        var openQuestions = turn.CoverageUpdate.Questions;

        if (!string.IsNullOrWhiteSpace(turn.CoverageUpdate.Map))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.CoverageMap(turn.CoverageUpdate.Map)));

        if (openQuestions.Any(q => q.IsOpen))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.OpenQuestions(openQuestions)));

        AppendTableBlocks(messages, turn);
        AppendInterviewClosing(messages, turn);

        // KHÔNG còn khối "## Các câu hỏi BẠN ĐÃ HỎI ở những lượt trước" ở đây. Nó từng liệt kê lại các
        // câu cũ ngay trước transcript, nhưng danh sách ấy dựng từ CHÍNH `turn.Recent` — đúng cái list mà
        // AppendTranscript ngay dưới gửi nguyên văn — nên nó là một bản chép đôi trọn vẹn, không bao giờ
        // chở thêm được một câu nào (AskedQuestionHistory.Collect đọc `Message` và cột `Questions`, mà
        // BuildAssistantContext echo lại cả hai). Đo trên log BAChat 2026-09-05: 16 dòng của khối ↔ đúng
        // 16 lượt assistant của transcript, ~5.000 ký tự trả tiền hai lần mỗi lượt — và trả GIÁ ĐẦY ĐỦ,
        // vì khối này đổi sau mỗi lượt nên nằm ngoài prefix cache.
        //
        // Thứ giữ cho việc bỏ này an toàn là encoder ở BuildAssistantContext: transcript chỉ thay thế
        // được khối kia khi model ĐỌC được nó. Chừng nào lượt assistant còn đi lên dưới dạng
        // "C\u1EA3m \u01A1n…" thì câu hỏi cũ coi như không có mặt. Hai thay đổi đó là một cặp.
        //
        // Phanh TẤT ĐỊNH không đụng tới: ApplyRepeatedQuestionBrake vẫn đọc `turn.AskedBefore` và vẫn
        // loại câu trùng khỏi lượt trả lời.
        AppendTranscript(messages, turn);
        return messages;
    }

    /// <summary>
    /// Ngữ cảnh của lượt ở chế độ YÊU CẦU THAY ĐỔI (<see cref="RequirementChangeMode"/>): prompt riêng gọn
    /// hơn nhiều, bản Brief ĐÃ DUYỆT thay cho bản đồ bao phủ + bốn bảng đã chốt, bối cảnh tổ chức rút gọn,
    /// mục lục màn hình thay cho phạm vi màn hình đầy đủ, và chỉ các lượt sau mốc duyệt đi nguyên văn
    /// (phần trước đã được gộp vào tóm tắt — xem <see cref="BuildTurnContextAsync"/>).
    ///
    /// <para>
    /// Bỏ hẳn: bản đồ bao phủ, bảng luồng/báo cáo/phân quyền/thông báo, checklist học được, khối "CHẶNG VỀ
    /// ĐÍCH". Luật "không hỏi phân quyền / thông báo / giao diện" mà các khối bảng chở nay nằm trong chính
    /// prompt <c>requirement-change.v1.md</c>. Thứ tự vẫn theo độ biến động tăng dần (prompt cache).
    /// </para>
    /// </summary>
    private async Task<List<ChatMessage>> BuildChangeRequestMessagesAsync(TurnContext turn, CancellationToken cancellationToken)
    {
        var project = turn.Project;
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, _promptTemplateService.Get(ChangeRequestPromptKey)).AsCacheBreakpoint()
        };

        var organizationContext = await _orgContext.BuildChangeRequestContextAsync(project.OrgUnitCode, cancellationToken);
        if (!string.IsNullOrWhiteSpace(organizationContext))
            messages.Add(new ChatMessage(ChatRole.System, organizationContext));

        if (!string.IsNullOrWhiteSpace(turn.UserMemory))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.UserProfile(turn.UserMemory)));

        var brief = turn.ApprovedBrief!;
        messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.ApprovedBrief(brief.VersionName, brief.Content)).AsCacheBreakpoint());

        if (turn.SourceTextInPrefix)
            messages.Add(new ChatMessage(ChatRole.System, turn.SourceContents.TextOnly()));

        if (!string.IsNullOrWhiteSpace(turn.Memory.Summary))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.ConversationMemory(turn.Memory.Summary)));
        messages[^1].AsCacheBreakpoint();

        var screenIndex = UxBlueprintRenderer.RenderScreenIndex(project.UxBlueprint);
        if (!string.IsNullOrWhiteSpace(screenIndex))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.ScreenIndex(screenIndex)));

        var openQuestions = turn.CoverageUpdate.Questions;
        if (openQuestions.Any(q => q.IsOpen))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.OpenQuestions(openQuestions)));

        AppendTranscript(messages, turn);
        return messages;
    }

    /// <summary>Prompt nền của chế độ yêu cầu thay đổi.</summary>
    public const string ChangeRequestPromptKey = "BusinessAnalyst/requirement-change.v1.md";

    /// <summary>
    /// Các khối bảng, chia làm hai loại: bảng ĐÃ CHỐT (đính vào mọi lượt sau, không phụ thuộc cổng nào đang
    /// mở) và nhiều nhất MỘT khối "## LƯỢT NÀY:" của cổng đang mở (bảng luồng), đến từ một lời gọi <see cref="InterviewTableGate.Select"/>.
    ///
    /// <para>
    /// Nửa LUẬT của khối "## LƯỢT NÀY:" nạp từ prompt riêng của từng bảng, và CHỈ nạp ở đúng lượt cổng của
    /// nó mở. Vị trí đính cũng là một quyết định về CHI PHÍ: khối này nằm ở vùng biến động của danh sách
    /// message, SAU khối tài liệu nguồn tĩnh, nên prefix cache không mất gì (xem ghi chú prompt cache ở
    /// <see cref="BuildMessagesAsync"/>).
    /// </para>
    /// </summary>
    private void AppendTableBlocks(List<ChatMessage> messages, TurnContext turn)
    {
        var project = turn.Project;

        AppendConfirmedTable(messages, FlowMapBuilder.RenderConfirmedBlock(project.FlowMap), BAChatPromptBlocks.ConfirmedFlowMap);
        AppendConfirmedTable(messages, UxBlueprintRenderer.RenderChatBlock(project.UxBlueprint), BAChatPromptBlocks.ConfirmedUxBlueprint);
        AppendConfirmedTable(messages, ReportMapBuilder.RenderConfirmedBlock(project.ReportMap), BAChatPromptBlocks.ConfirmedReportMap);

        // PHÂN QUYỀN — hai trạng thái, hai lệnh khác nhau, và lệnh nào cũng do CƠ CHẾ chọn chứ không để model
        // tự đoán: đã có bảng (BA soạn ở bước "Write Requirement", người dùng sửa được ở popup) / chưa (cấm hỏi lẻ).
        var confirmedMatrix = PermissionMatrixBuilder.RenderConfirmedBlock(project.PermissionMatrix);
        if (!string.IsNullOrWhiteSpace(confirmedMatrix))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.ConfirmedPermissionMatrix(confirmedMatrix)));
        else
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.PermissionMatrixDeferred));

        if (turn.Table == InterviewTableKind.FlowMap)
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.FlowMapTable(
                _promptTemplateService.Get(BAChatPromptBlocks.FlowMapPromptKey))));

        // THÔNG BÁO / NHẮC NHỞ — lệnh KHÔNG HỎI vô điều kiện. Lệnh này không cần đường thoát nào: nhóm không
        // bao giờ chặn cổng readiness (CoverageUnaskedGroupGuard đặt nó về trạng thái không chặn ở mọi lượt
        // distill), nên cấm hỏi không khóa chết được nút "Write Requirement".
        messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.NotificationDeferred));
    }

    /// <summary>
    /// Đính khối "## CHẶNG VỀ ĐÍCH" khi buổi phỏng vấn đã quá ngân sách lượt mà bản đồ còn nhóm hở phải
    /// đóng bằng câu hỏi. Ba điều kiện, thiếu một là không đính:
    ///
    /// <list type="bullet">
    ///   <item>đã quá ngân sách (<see cref="InterviewPaceBudget.IsClosing"/>);</item>
    ///   <item>lượt này KHÔNG bày bảng — lượt bày bảng đã mang một khối "## LƯỢT NÀY:", thêm một mệnh
    ///   lệnh nữa là hai mệnh lệnh chọi nhau (cùng lý do <see cref="InterviewTableGate"/> chỉ chọn một
    ///   bảng mỗi lượt);</item>
    ///   <item>còn ít nhất một nhóm hở được phép hỏi — hết rồi thì một khối giục "đóng các nhóm còn hở" với
    ///   danh sách rỗng chỉ đẩy model sang hỏi lẻ đúng nhóm nó bị cấm hỏi.</item>
    /// </list>
    ///
    /// Đứng SAU các khối bảng: nó thuộc vùng biến động của danh sách message (đổi theo số lượt), nên không
    /// đụng tới prefix cache — xem ghi chú ở <see cref="BuildMessagesAsync"/>.
    /// </summary>
    private static void AppendInterviewClosing(List<ChatMessage> messages, TurnContext turn)
    {
        if (!InterviewPaceBudget.IsClosing(turn.UserTurnCount) || turn.Table != InterviewTableKind.None)
            return;

        var groups = InterviewPaceBudget.GroupsToClose(turn.Project.RequirementCoverageMap);
        if (groups.Count == 0)
            return;

        messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.InterviewClosing(turn.UserTurnCount, groups)));
    }

    /// <summary>
    /// Các lượt hội thoại gần đây, dựng lại đúng hình dạng model được yêu cầu xuất (xem
    /// <see cref="BuildAssistantContext"/>). Tài liệu nguồn đi kèm ĐÚNG lượt user cuối, và chỉ khi phần
    /// text của chúng chưa được tách lên đầu prompt.
    /// </summary>
    private static void AppendTranscript(List<ChatMessage> messages, TurnContext turn)
    {
        var recent = turn.Recent;
        for (var i = 0; i < recent.Count; i++)
        {
            var c = recent[i];
            // Lượt ⚠️ đóng một lượt đã vỡ KHÔNG đi lên model: nó không phải lời BA, và echo lại nó dưới
            // dạng {message: "⚠️ …"} là dạy model rằng lượt trước của nó là một lỗi API — xem
            // ConversationTurnRenderer.IsFailureTurn. Bỏ qua bằng `continue` nên chỉ số `i` của các lượt
            // còn lại (LastUserIndex) không xê dịch.
            if (ConversationTurnRenderer.IsFailureTurn(c))
                continue;

            var isAssistant = c.Role == "assistant";
            // Lượt cũ của BA được "dựng lại" đúng JSON {message, suggestions}. Nếu chỉ đưa text thuần,
            // model thấy phản hồi trước của mình là văn xuôi và bắt chước → bỏ JSON từ lượt 2 trở đi,
            // mất luôn gợi ý. Đưa lại đúng format giúp model giữ JSON ở mọi lượt.
            var text = isAssistant ? BuildAssistantContext(c) : c.Message;

            // SourceTextInPrefix ⇒ khối nguồn đã đi ở đầu prompt rồi, đính lại đây là chép đôi.
            if (!isAssistant && i == turn.LastUserIndex && turn.SourceContents.Count > 0 && !turn.SourceTextInPrefix)
            {
                var contents = new List<AIContent> { new TextContent(text) };
                contents.AddRange(turn.SourceContents.Contents);
                messages.Add(new ChatMessage(ChatRole.User, contents));
            }
            else
            {
                messages.Add(new ChatMessage(isAssistant ? ChatRole.Assistant : ChatRole.User, text));
            }
        }
    }

    /// <summary>
    /// Nắn lượt model vừa trả thành lượt sẽ được LƯU. Các chốt chặn TẤT ĐỊNH chạy nối tiếp theo ĐÚNG thứ
    /// tự dưới đây, và thứ tự là một phần của hành vi: chúng viết đè lên nhau, nên chốt chặn "lượt câm"
    /// phải chạy cuối — nó xét HÌNH DẠNG của lượt đã chốt chứ không xét ý định của model.
    /// </summary>
    private async Task ShapeTurnAsync(
        BAChatTurnDraft draft, BAChatReply parsedReply,
        TurnContext turn, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        ApplyModelReply(draft, parsedReply);

        // LƯỢT TRỌNG TÀI — một lời gọi LLM ngắn đọc lượt vừa nắn và trả về các phán quyết về NGHĨA mà ba
        // chốt chặn dưới đây cần: câu nào là hỏi lại, chip nào không chở câu trả lời, bộ chip nào chỉ là
        // một cặp có/không, người dùng có vừa nhắc tới một file đang dùng không. Chạy MỘT lần, trước mọi chốt chặn, để cả ba cùng đọc một
        // phán quyết về đúng lượt model đã viết. Fail-open: lỗi ⇒ phán quyết rỗng ⇒ không ai động tay.
        var verdict = await JudgeTurnAsync(draft, turn, ba, model, cancellationToken);
        draft.JudgedAsFileRequest = verdict.AsksForFile;
        draft.UserMentionsSource = verdict.UserMentionsExistingSource;

        ApplyEmptyChipVerdict(draft, verdict);
        var openedYesNo = InterviewQuestionRules.OpenGroupClosingYesNo(draft.Questions, verdict.YesNoChips);
        ApplyRepeatedQuestionBrake(draft, parsedReply, turn, verdict);
        if (openedYesNo)
            ApplyAskAloneRule(draft);
        ApplyReadinessGate(draft, turn);
        BuildInterviewTable(draft, parsedReply, turn);
        ApplySourceRequestTurn(draft, turn, verdict);
        ApplySummaryCheckChips(draft);

        // LƯỢT CÂM — chốt chặn cuối cùng của lượt chat, chạy SAU mọi nhánh trên vì nó xét HÌNH DẠNG của
        // lượt đã chốt chứ không xét ý định của model.
        //
        // Hai phanh tất định phía trên chỉ soi các lượt CÓ hỏi: lượt mà mọi câu hỏi đều là câu đã hỏi
        // (AskedQuestionHistory), và lượt mời bấm "Write Requirement" quá sớm (cổng readiness). Còn một
        // hình dạng thứ ba lọt qua cả hai — lượt KHÔNG hỏi gì cả. Người dùng không có chỗ nào để trả lời,
        // và vì lượt đó không chở câu hỏi nào nên phanh chống hỏi lại (so theo nội dung CÂU HỎI) cũng
        // không nhìn thấy nó.
        //
        // Ca thật (dự án JD Libary 5, các lượt 82/84/90): bản đồ kẹt [MỘT PHẦN] ở một dòng mà người dùng
        // ĐÃ trả lời, nên BA hết đường hợp lệ — prompt cấm hỏi lại điều vừa được trả lời, và cấm nhắc tới
        // nút khi bản đồ chưa đủ — rồi rơi vào "mình tiếp tục bước rà soát cuối", một bước không hề tồn
        // tại ở chế độ chat. Người dùng đáp "ok", "tiếp tục đi" và nhận lại đúng một lượt như thế: ba lượt
        // bị đốt, bản đồ không nhúc nhích, cuộc phỏng vấn đứng hẳn ở lượt cuối. Đây đúng là ca mà bản đồ
        // KHÔNG tự lành được: nó chỉ nhúc nhích khi có thông tin mới, mà lượt câm thì không hỏi được gì để
        // lấy thông tin mới.
        //
        // Thay bằng đúng bước kế tiếp tất định của đường lượt-trùng: bản đồ còn thiếu ⇒ câu chặn của cổng
        // (đường phát thứ tư của nó), bản đồ đã đủ ⇒ lời mời bấm nút. Cả hai đều là thứ người dùng trả
        // lời được. Xem BAChatTurnDraft.IsSilent cho ranh giới "thế nào là câm".
        if (draft.IsSilent)
        {
            var followUp = BuildFollowUpAfterRepeat(
                turn.Project, turn.CoverageUpdate.Questions, turn.Recent);
            draft.Reply = followUp.Message;
            draft.OpenEnded = followUp.OpenEnded;
            // Bước kế tất định có thể CHÍNH LÀ lời mời (bản đồ đã đủ) — cờ phải đi theo câu, nếu không
            // lượt này mời bấm một cái nút mà cổng không mở.
            draft.InvitesWriteRequirement = followUp.Invites;
        }
    }

    /// <summary>Lượt model trả về, đã chuẩn hoá — điểm xuất phát của mọi chốt chặn phía sau.</summary>
    private static void ApplyModelReply(BAChatTurnDraft draft, BAChatReply parsedReply)
    {
        // Lượt phải gửi lại mà bỏ ảnh có kèm một dòng dặn dò NỘI BỘ cho model; model yếu hay chép
        // nguyên văn dòng đó vào câu trả lời. Dọn trước khi nó thành một lượt hội thoại.
        // Model trả về message rỗng: KHÔNG mời bấm nút ở đây nữa. Câu dự phòng cũ ("…hoặc bấm
        // \"Write Requirement\" để tạo tài liệu.") mời bấm một cái nút mà cổng chưa chắc đã mở — từ khi cổng
        // đi theo cờ `ready` chứ không theo mặt chữ, một câu như thế là lời mời không có nút. Câu trung tính
        // này rơi thẳng vào chốt chặn LƯỢT CÂM ở cuối, và chỗ đó thay nó bằng đúng bước kế tất định: hỏi
        // nhóm còn thiếu, hoặc mời bấm nút KÈM cờ khi bản đồ đã đủ.
        //
        // Lượt `ready` thì message rỗng là ĐÚNG HÌNH DẠNG prompt dạy, không phải model quên: bong bóng lời
        // mời không bao giờ được vẽ (RequirementReadinessGate.InviteTurnToHide — thẻ #summaryGate nói thay
        // kèm cái nút), nên một đoạn tóm tắt/lời mời do model viết chỉ tốn token output. Điền một câu mời
        // CỐ ĐỊNH để lượt lưu xuống vẫn đọc đúng ở những chỗ còn hiện nó: transcript, bản xuất chat, và bong
        // bóng lịch sử khi người dùng nhắn tiếp sau lời mời. Cờ bị cổng readiness hạ ⇒ Replace đè câu này.
        draft.Reply = string.IsNullOrWhiteSpace(parsedReply.Message)
            ? parsedReply.Ready ? Loc.T("BAChatService.ApplyModelReply.2") : Loc.T("BAChatService.ApplyModelReply")
            : EndpointQuirks.StripInternalNotices(parsedReply.Message);

        // Lưu suggestions tách riêng (JSON) để UI render chip; chỉ set khi thực sự có gợi ý.
        if (parsedReply.Suggestions.Count > 0)
        {
            draft.SuggestionsJson = JsonSerializer.Serialize(parsedReply.Suggestions);
            draft.SuggestionsMultiSelect = parsedReply.MultiSelect;
        }

        // Normalize đã đảm bảo OpenEnded ⇒ Suggestions rỗng, nên hai nhánh này loại trừ nhau.
        draft.OpenEnded = parsedReply.OpenEnded;
        draft.SummaryCheck = parsedReply.SummaryCheck;

        // ĐỀ NGHỊ của model rằng đã đủ vốn để soạn tài liệu. Nhận vào đây, nhưng chỉ đứng được nếu qua
        // ApplyReadinessGate ngay bên dưới — model tự chấm mình thì không phải một cổng.
        draft.InvitesWriteRequirement = parsedReply.Ready;

        // Lượt hỏi GỘP (2–4 câu độc lập): Normalize đã đảm bảo hoặc có Questions, hoặc có
        // Suggestions — không bao giờ cả hai.
        draft.Questions = parsedReply.Questions;
    }

    /// <summary>
    /// Gọi lượt trọng tài cho lượt vừa nắn. Câu đem xét đánh số theo ĐÚNG thứ tự mà các chốt chặn đọc lại:
    /// lượt gộp thì mỗi phần tử của <c>draft.Questions</c> là một câu; lượt một câu thì cả <c>Reply</c> là
    /// câu số 1 (nội dung lượt CHỞ câu hỏi ở đường này), chip của lượt là chip của nó.
    /// <para>
    /// Lượt MỜI bấm "Write Requirement" không đem xét: nó không hỏi gì, và cổng readiness đã là chốt chặn
    /// của nó. Không còn gì để phán (không câu hỏi, cũng không còn cửa cho lượt xin file) ⇒ không gọi.
    /// </para>
    /// </summary>
    private async Task<TurnJudgeVerdict> JudgeTurnAsync(
        BAChatTurnDraft draft, TurnContext turn, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        if (_judge == null)
            return new TurnJudgeVerdict();

        var questions = new List<InterviewJudge.TurnQuestion>();
        if (draft.Questions.Count > 0)
        {
            questions.AddRange(draft.Questions.Select(q => new InterviewJudge.TurnQuestion(q.Question, q.Suggestions)));
        }
        else if (!draft.InvitesWriteRequirement && !string.IsNullOrWhiteSpace(draft.Reply))
        {
            questions.Add(new InterviewJudge.TurnQuestion(draft.Reply, ParseChips(draft.SuggestionsJson)));
        }

        if (questions.Count == 0 && !SourceRequestStillPossible(turn))
            return new TurnJudgeVerdict();

        var lastUserMessage = turn.LastUserIndex >= 0 ? turn.Recent[turn.LastUserIndex].Message : null;
        return await _judge.JudgeTurnAsync(
            turn.ProjectId, turn.AskedBefore, lastUserMessage, questions, draft.SummaryCheck, ba, model, cancellationToken);
    }

    private static List<string> ParseChips(string? suggestionsJson)
        => string.IsNullOrEmpty(suggestionsJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(suggestionsJson) ?? new List<string>();

    /// <summary>
    /// CHIP RỖNG — "Khác", "Tự nhập", "Quy tắc khác", "Mình mô tả cụ thể hơn", "Other": chip mà toàn bộ nội
    /// dung chỉ là "không phải mấy cái kia" hoặc "để tôi tự nói". Nó nói ĐÚNG BẰNG ô "Ý khác" nằm ngay dưới
    /// mọi hàng chip, chỉ thiếu đúng phần đắt nhất: NỘI DUNG. Mà ở lượt một câu, bấm chip là GỬI NGAY — cú
    /// bấm đó gửi đi một lượt user rỗng ("Quy tắc khác", quy tắc gì thì không ai biết), trong khi bản đồ
    /// bao phủ vẫn tính là nhóm đó đã được hỏi VÀ đã trả lời.
    ///
    /// <para>
    /// Chip nào rỗng là do <see cref="InterviewJudge"/> phán. Ở đây từng là một phép dò HÌNH DẠNG trong
    /// <c>BAChatReplyParser</c> (đuôi "khác" + một danh từ mê-ta, hoặc ngôi thứ nhất + động từ diễn đạt) —
    /// sáu bảng cụm tiếng Việt, cố ý HẸP vì mỗi lần đoán sai là xoá một câu trả lời thật, nên vừa lọt lưới
    /// thường xuyên vừa hoàn toàn câm với mọi ngôn ngữ khác.
    /// </para>
    ///
    /// <para>
    /// Hai chốt giữ cho phép xoá này không quá tay, cả hai đều là HÌNH DẠNG nên ở lại trong code:
    /// chỉ xoá chip khớp NGUYÊN VĂN một chip đang có (trọng tài không được phép bịa ra chip để xoá), và
    /// xoá xong phải còn ≥ 2 chip. Bộ HAI chip mà prompt kê sẵn ở lượt xin chốt (["Đồng ý", "Tôi muốn
    /// khác"]) thì vế "khác" là một trong hai nhánh trả lời của chính câu hỏi; xoá nó đi là biến câu hỏi
    /// thành cái gật bắt buộc. Ở đúng bộ đó, việc mở ô nhập tại chỗ là của giao diện (requirements.js:
    /// isDissentChip).
    /// </para>
    /// </summary>
    private static void ApplyEmptyChipVerdict(BAChatTurnDraft draft, TurnJudgeVerdict verdict)
    {
        if (verdict.EmptyChips.Count == 0)
            return;

        if (draft.Questions.Count > 0)
        {
            for (var i = 0; i < draft.Questions.Count; i++)
            {
                var question = draft.Questions[i];
                var kept = DropEmptyChips(question.Suggestions, verdict, i + 1);
                if (kept == null)
                    continue;

                question.Suggestions = kept;
                question.MultiSelect = question.MultiSelect && kept.Count >= 2;
            }

            return;
        }

        var chips = DropEmptyChips(ParseChips(draft.SuggestionsJson), verdict, 1);
        if (chips != null)
            draft.SuggestionsJson = JsonSerializer.Serialize(chips);
    }

    /// <summary>Bộ chip sau khi bỏ chip rỗng của câu số <paramref name="number"/>; <c>null</c> = giữ nguyên.</summary>
    private static List<string>? DropEmptyChips(List<string> chips, TurnJudgeVerdict verdict, int number)
    {
        var empty = verdict.EmptyChips
            .Where(x => x.Question == number)
            .SelectMany(x => x.Chips)
            .Select(AskedQuestionHistory.Key)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (empty.Count == 0)
            return null;

        var kept = chips.Where(chip => !empty.Contains(AskedQuestionHistory.Key(chip))).ToList();
        return kept.Count >= 2 && kept.Count < chips.Count ? kept : null;
    }

    /// <summary>
    /// Câu ngoại lệ vừa bị <see cref="InterviewQuestionRules.OpenGroupClosingYesNo"/> đổi thành câu MỞ thì
    /// từ giây đó nó là câu ĐÀO — câu bắt buộc đứng một mình trong lượt. <c>BAChatReplyParser.Normalize</c>
    /// đã áp luật hỏi-một-mình lúc nhận lượt model, nhưng lúc đó câu này còn mang cặp chip có/không nên
    /// chưa bị tính; chạy lại đúng hàm ấy trên các câu còn lại của lượt để một luật không có hai bản.
    ///
    /// <para>
    /// Chạy SAU phanh chống hỏi lại, và thứ tự đó là bắt buộc: phanh đọc phán quyết theo SỐ THỨ TỰ câu,
    /// nên không bước nào trước nó được phép bỏ bớt câu. Phanh đã thay cả lượt bằng một câu dựng sẵn
    /// (không còn câu gộp nào) thì ở đây không còn gì để làm.
    /// </para>
    /// </summary>
    private void ApplyAskAloneRule(BAChatTurnDraft draft)
    {
        if (draft.Questions.Count < 2 || !draft.Questions.Any(InterviewQuestionRules.MustAskAlone))
            return;

        ApplyTrimmedQuestions(draft, _replyParser.Normalize(new BAChatReply
        {
            Message = draft.Reply,
            Questions = draft.Questions
        }));
    }

    /// <summary>Ghi một lượt gộp đã được chuẩn hoá lại (bớt câu) vào bản nháp — lượt còn câu để hỏi.</summary>
    private static void ApplyTrimmedQuestions(BAChatTurnDraft draft, BAChatReply trimmed)
    {
        draft.Reply = trimmed.Message;
        draft.SuggestionsJson = trimmed.Suggestions.Count > 0 ? JsonSerializer.Serialize(trimmed.Suggestions) : null;
        draft.SuggestionsMultiSelect = trimmed.MultiSelect && trimmed.Suggestions.Count > 0;
        draft.OpenEnded = trimmed.OpenEnded;
        // Lượt còn câu để hỏi ⇒ không phải lời mời, dù model có khai `ready` hay không.
        draft.InvitesWriteRequirement = false;
        draft.Questions = trimmed.Questions;
    }

    /// <summary>
    /// PHANH CHỐNG HỎI LẠI. Prompt đã cấm phát lại câu cũ, nhưng bản đồ bao phủ — thứ dẫn dắt lượt hỏi —
    /// chỉ có độ phân giải theo NHÓM: một dòng chưa đạt chuẩn [RÕ] (hoặc một lượt chắt lọc bản đồ hỏng,
    /// giữ nguyên bản cũ) là đủ để model phát lại cả cụm câu hỏi của lượt trước, kèm chip gợi ý chính là
    /// câu trả lời người dùng vừa gõ. Ở đây câu trùng bị LOẠI khỏi lượt trả lời trước khi nó kịp lên màn
    /// hình.
    ///
    /// <para>
    /// <b>"Hỏi lại" là xin lại thông tin người dùng ĐÃ đưa</b> — không phải đặt lại một câu đã từng đặt. Câu
    /// người dùng bỏ qua thì chưa có thông tin nào, nên hỏi lại nó (một lần) là đúng việc; chỉ khi họ đã lờ
    /// nó hai lần thì lần ba mới là giục. Phân biệt được hai ca đó cần đọc CÂU ĐÁP, và đó là việc của
    /// <see cref="InterviewJudge"/> (<paramref name="verdict"/>): nó nhận sổ theo lượt kèm câu người dùng đáp
    /// (<see cref="AskedQuestionHistory.CollectTurns"/>).
    /// </para>
    /// <para>
    /// Vì vậy phán quyết của trọng tài là NGUỒN DUY NHẤT khi nó có mặt
    /// (<see cref="TurnJudgeVerdict.RepeatsJudged"/>). Lưới khớp NGUYÊN VĂN
    /// (<see cref="AskedQuestionHistory.IsRepeat"/>) chỉ là đường lùi khi trọng tài vắng: nó biết câu đã được
    /// đặt chứ không biết câu đã được trả lời, nên để nó chạy song song là chặn đúng câu người dùng bỏ qua
    /// mà BA hỏi lại y nguyên. Sổ rỗng thì phán quyết bị bỏ qua — chưa hỏi câu nào thì không có gì để hỏi
    /// lại, và một con số trọng tài bịa ra không được phép xoá câu hỏi đầu tiên của buổi.
    /// </para>
    /// <para>
    /// Lời XIN FILE ĐANG ĐẾN HẠN (<see cref="IsDueFileRequest"/>) đứng ngoài phanh: nó không xin lại thông
    /// tin nào — nó xin chính cái file người dùng vừa nói là có — và nếu bị thay đi thì
    /// <see cref="ApplySourceRequestTurn"/> ngay sau cũng chỉ đặt lại một lời xin dựng sẵn, kém ngữ cảnh hơn
    /// câu model vừa viết.
    /// </para>
    /// </summary>
    private void ApplyRepeatedQuestionBrake(
        BAChatTurnDraft draft, BAChatReply parsedReply, TurnContext turn, TurnJudgeVerdict verdict)
    {
        var askedKeys = AskedQuestionHistory.Keys(AskedQuestionHistory.Questions(turn.AskedBefore));
        var judged = verdict.RepeatsJudged && askedKeys.Count > 0;
        var judgedRepeats = judged ? verdict.Repeats.ToHashSet() : new HashSet<int>();
        bool IsRepeat(string? question, int number) => judged
            ? judgedRepeats.Contains(number)
            : AskedQuestionHistory.IsRepeat(question, askedKeys);
        // Cụm tín hiệu tái mở nay nằm trong CÂU HỎI của nhóm, nên bản đồ phải được gắn câu hỏi vào thì
        // ReopenedGroups mới đọc ra được — xem CoverageMapParser.AttachQuestions.
        var reopenedGroups = AskedQuestionHistory.ReopenedGroups(CoverageMapParser.AttachQuestions(
            CoverageMapParser.Parse(turn.Project.RequirementCoverageMap), turn.CoverageUpdate.Questions));

        if (draft.Questions.Count > 0)
        {
            var kept = draft.Questions
                .Where((q, i) => AskedQuestionHistory.IsExempt(q, reopenedGroups) || !IsRepeat(q.Question, i + 1))
                .ToList();

            if (kept.Count == draft.Questions.Count)
                return;

            // Câu dẫn của lượt gộp thường tự đếm ("dưới đây là 4 câu xác nhận") nên bỏ bớt câu là
            // nó nói sai — thay bằng câu dẫn trung tính. Còn đúng một câu thì để Message rỗng cho
            // Normalize nâng chính câu hỏi lên làm nội dung lượt (đường một-câu).
            var trimmed = _replyParser.Normalize(new BAChatReply
            {
                Message = kept.Count >= 2 ? Loc.T("BAChatService.ApplyRepeatedQuestionBrake") : string.Empty,
                Questions = kept
            });

            if (kept.Count == 0)
            {
                // Không còn câu nào MỚI để hỏi: lượt này lẽ ra rỗng. Thay bằng bước kế tiếp TẤT
                // ĐỊNH suy từ bản đồ (hỏi đúng nhóm còn thiếu, hoặc mời bấm nút khi bản đồ đã đủ)
                // — im lặng hoặc để nguyên câu dẫn cụt đều tệ hơn. Câu đó không có chip, nên cờ
                // "câu mở" đi theo nó để ô nhập nhận vai chỗ trả lời. Chủ đề để cổng bám vào là
                // chính các câu vừa bị loại — `draft.Questions` ở đây còn là bản GỐC, phần gán bản
                // đã lọc nằm ở cuối nhánh.
                var blocked = string.Join(" ", draft.Questions.Select(q => q.Question));
                var followUp = BuildFollowUpAfterRepeat(
                    turn.Project, turn.CoverageUpdate.Questions, turn.Recent, blocked);
                draft.Replace(followUp.Message, followUp.OpenEnded, followUp.Invites);
            }
            else
            {
                ApplyTrimmedQuestions(draft, trimmed);
            }

            draft.Questions = trimmed.Questions;
            return;
        }

        // Lượt này có thật sự đang HỎI không — phép thử DÙNG CHUNG với phía ghi sổ
        // (AskedQuestionHistory.Collect), và bắt buộc phải dùng chung: hai phía lệch nhau thì cả một lớp
        // câu hỏi được ghi vào sổ mà không bao giờ bị soi lại. Xem AskedQuestionHistory.IsAskingTurn cho
        // ca thật. "Chỗ trả lời" ở phía này gồm cả cờ câu-mở, thứ không đọc lại được từ một lượt đã lưu.
        //
        // Lượt một câu là câu số 1 của phán quyết (xem JudgeTurnAsync). Trọng tài được cho biết lượt nào
        // là NHỊP TÓM TẮT KIỂM CHỨNG (cờ `summaryCheck` do model khai): nội dung của nhịp đó là đoạn tóm
        // tắt, còn câu xin gật ở cuối thì prompt bắt giống nhau ở mọi nhịp — ca thật (Locker management
        // 7): bản tóm tắt kết bằng "Anh/chị xác nhận cách hiểu này đúng chưa ạ?" từng bị coi là hỏi lại
        // câu chốt một kịch bản KHÁC từ ba lượt trước, và người dùng nhận về câu chặn dựng sẵn của cổng
        // thay cho bản tóm tắt. Phân biệt "xin gật cho một nội dung khác" với "hỏi lại" là việc của NGHĨA,
        // nên nó nằm ở turn-judge.v1.md chứ không còn là một bảng cụm xin gật ở đây.
        if (AskedQuestionHistory.IsAskingTurn(draft.Reply, parsedReply.Suggestions.Count > 0 || parsedReply.OpenEnded)
            && !draft.InvitesWriteRequirement
            && !IsDueFileRequest(draft, turn, verdict)
            && IsRepeat(draft.Reply, 1))
        {
            // Lượt hỏi MỘT câu, và chính câu đó đã hỏi rồi (Message chở câu hỏi ở đường này).
            var followUp = BuildFollowUpAfterRepeat(
                turn.Project, turn.CoverageUpdate.Questions, turn.Recent, draft.Reply);
            draft.Replace(followUp.Message, followUp.OpenEnded, followUp.Invites);
        }
    }

    /// <summary>
    /// Lượt MỜI bấm "Write Requirement" phải qua cổng readiness TẤT ĐỊNH ngay tại đây, trước khi người
    /// dùng nhìn thấy lời mời: ready suy thẳng từ bản đồ bao phủ (đã gộp tới lượt user mới nhất ở đầu lượt
    /// này) — cùng dữ liệu mà panel "Tiến độ khai thác" render, nên panel, lời mời và nút KHÔNG THỂ vênh
    /// nhau. Bản đồ chưa đủ (kể cả khi lượt gộp lỗi giữ bản cũ — fail-closed, lượt sau gộp bù) ⇒ thay lời
    /// mời bằng câu hỏi nêu đúng nhóm còn thiếu, nút vẫn mờ; đủ ⇒ giữ lời mời và bước sinh tài liệu KHÔNG
    /// xét lại trên cùng transcript (xem ProductBriefDraftService.GenerateOrUpdateDraftAsync). Một nguồn
    /// chân lý, một tiêu chuẩn.
    /// </summary>
    private static void ApplyReadinessGate(BAChatTurnDraft draft, TurnContext turn)
    {
        if (!draft.InvitesWriteRequirement)
            return;

        // CỜ NÓI "ĐỦ RỒI", NỘI DUNG LẠI ĐANG HỎI ⇒ TIN NỘI DUNG, HẠ CỜ. Đây là chốt chặn thay cho thứ mà
        // phép dò chuỗi cũ vô tình làm được: hồi đó cổng chỉ mở khi chính câu chữ mời bấm nút, nên một lượt
        // `ready: true` mà thật ra đang hỏi thì cổng vẫn đóng và câu hỏi vẫn lên màn hình. Nay cổng đi theo
        // cờ, nên nếu không có nhánh này thì đúng ca đó mở cổng — và vì lượt mời được cổng nói thay
        // (RequirementReadinessGate.InviteTurnToHide) nên câu hỏi ấy còn bị GIẤU luôn: người dùng mất
        // trắng một câu hỏi và chỉ thấy một cái nút.
        //
        // Phép thử là phép thử DÙNG CHUNG của phanh chống hỏi lại (dấu hỏi, hoặc có sẵn chỗ trả lời) — cùng
        // một định nghĩa "thế nào là đang hỏi" cho cả hai chốt chặn. Lời mời hợp lệ đi qua được: prompt bắt
        // lượt `ready` phải rỗng chip, rỗng thẻ hỏi, và câu mời thì không có dấu hỏi. Lượt mời lỡ kết bằng
        // dấu hỏi bị hạ cờ ⇒ cổng đóng thêm ĐÚNG một lượt, rồi bước kế tất định mở lại — chậm một nhịp,
        // không mất gì.
        if (AskedQuestionHistory.IsAskingTurn(
                draft.Reply,
                draft.SuggestionsJson != null || draft.OpenEnded || draft.Questions.Count > 0))
        {
            draft.InvitesWriteRequirement = false;
            return;
        }

        // `Recent` đi kèm để cổng không phát lại đúng câu chặn nó vừa phát: câu của cổng không có chip nên
        // phanh chống hỏi lại dùng chung không thấy nó — xem RequirementReadinessGate.
        var readiness = RequirementReadinessGate.Evaluate(
            turn.Project, turn.CoverageUpdate.Questions, turn.Recent);
        if (!readiness.Ready)
        {
            // Câu hỏi của gate xin một mẩu thông tin còn thiếu và không kèm chip nào ⇒ ô nhập là chỗ trả
            // lời DUY NHẤT: bỏ cờ multi của lời mời bị thay, bật cờ "câu mở" để khung chat mời người dùng
            // gõ, và cũng không giữ thẻ hỏi gộp — để lại thẻ cũ thì màn hình có hai lượt hỏi khác nhau
            // chồng lên nhau.
            draft.Replace(
                string.IsNullOrWhiteSpace(readiness.Message)
                    ? "Mình cần làm rõ thêm vài thông tin trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                    : readiness.Message,
                readiness.OpenEnded);
            return;
        }

        // Lời mời ĐƯỢC GIỮ. Hai dòng dưới là dây an toàn cho chốt chặn "đang hỏi" ở đầu hàm: một lời mời
        // kèm thẻ hỏi là tự mâu thuẫn ("không còn gì để hỏi" + 3 câu hỏi), và một lời mời mở ô "kể tự do"
        // thì mời hai việc cùng lúc. Chốt chặn kia đã loại sạch cả hai hình dạng ấy, nên đây là no-op —
        // giữ lại để một lần sửa chốt chặn kia không âm thầm thả chúng lên màn hình.
        draft.Questions = new List<BAChatQuestion>();
        draft.OpenEnded = false;
    }

    /// <summary>
    /// Dựng các DÒNG bảng của cổng đang mở từ đề xuất model — thuần dữ liệu, không gọi LLM, không đụng tới
    /// nội dung lượt. Tách riêng vì nó chạy ở HAI chỗ: vòng đòi lại của <see cref="CallForTurnAsync"/> (nơi
    /// "bảng có dựng được không" là điều kiện dừng) và bước nắn lượt ngay dưới. Gọi lại nhiều lần là an
    /// toàn — mỗi builder dựng một danh sách MỚI từ hạt giống trong <paramref name="turn"/> và không sửa
    /// gì trong đó.
    /// </summary>
    private static void FillTableRows(BAChatTurnDraft draft, BAChatReply parsedReply, TurnContext turn)
    {
        switch (turn.Table)
        {
            case InterviewTableKind.FlowMap:
                draft.FlowMap = FlowMapBuilder.Build(parsedReply.FlowMap);
                break;
        }
    }

    /// <summary>
    /// Đặt CÂU DẪN của bảng vừa dựng. Các dòng đã do <see cref="FillTableRows"/> dựng xong từ trong vòng gọi
    /// model. Chỉ chạy khi cổng đã mở (xem <see cref="InterviewTableGate"/>).
    ///
    /// <para>
    /// Model vẫn không trả bảng dùng được sau cả <see cref="MaxTableAttempts"/> lượt gọi ⇒ FAIL-OPEN: lượt đi
    /// tiếp không có bảng và cổng sẽ mở lại ở lượt sau. Chốt chặn lượt câm phía dưới là thứ đỡ lượt đó, nên
    /// nó không bao giờ ra màn hình dưới dạng một lượt không có chỗ trả lời.
    /// </para>
    /// </summary>
    private static void BuildInterviewTable(BAChatTurnDraft draft, BAChatReply parsedReply, TurnContext turn)
    {
        switch (turn.Table)
        {
            case InterviewTableKind.FlowMap:
                if (draft.FlowMap.Count > 0)
                    draft.TakeOverForTable(FlowMapIntro, parsedReply.Message, parsedReply.Ready);
                break;
        }
    }

    /// <summary>
    /// LƯỢT XIN FILE — người dùng vừa nhắc tới một file/bảng tính họ đang dùng mà dự án chưa có tài liệu
    /// nguồn nào. Luật "xin file NGAY TẠI LƯỢT ĐÓ" nằm trong prompt từ lâu và vẫn trượt im lặng (ca thật:
    /// JD Libary 5, người dùng nhắc hai file excel ở lượt 3 và 5, BA không xin lần nào trong 26 lượt) —
    /// nên nó phải là một chốt chặn của code như mọi luật đắt khác.
    ///
    /// <para>
    /// Thay TRỌN lượt chứ không chèn thêm một câu: xin file là lời nhờ HÀNH ĐỘNG, người dùng đọc xong đi
    /// tìm file và mọi thứ khác trong lượt rơi mất — trong khi bản đồ bao phủ vẫn tính là đã hỏi. Câu hỏi
    /// model vừa viết không mất đi đâu: nhóm của nó chưa nhúc nhích nên nó quay lại ở lượt sau, lúc đó đọc
    /// được file rồi thì thường còn hỏi ngắn hơn.
    /// </para>
    ///
    /// <para>
    /// Bốn điều kiện, và cả bốn đều cần: chưa có nguồn nào (có rồi thì đây là đường đọc lại file), lượt user
    /// CUỐI thật sự nhắc tới một vật mang dữ liệu (phán quyết của lượt trọng tài — xem InterviewJudge), chưa
    /// có lời xin ĐÍCH DANH nào (giục lần hai là phí lượt), và lượt này không phải lượt bày BẢNG (bảng là chỗ
    /// trả lời duy nhất của nó).
    /// </para>
    ///
    /// <para>
    /// Vế "chưa có lời xin đích danh" đọc CỜ đã lưu — cặp (lượt user <see cref="AgentConversation.MentionsSource"/>
    /// → lượt BA <see cref="AgentConversation.SourceRequested"/>), xem <see cref="SourceRequestTurn.AnsweredMention"/>
    /// — chứ không dò lại chữ của các lượt cũ. Bản cũ quét cụm "📎"/"đính kèm"/"gửi giúp" trên mọi lượt BA
    /// trong cửa sổ, tức app đoán lại chính chuỗi <see cref="SourceRequestTurn.Message"/> mà nó tự phát ra:
    /// một lượt vô tình có chữ "đính kèm" là khoá vĩnh viễn đường này. Bản sau đó đọc riêng
    /// <c>SourceRequested</c> và hỏng đúng kiểu ấy theo đường khác: lời mời chung chung của lượt mở đầu (luôn
    /// có nút 📎) đốt mất suất trước khi người dùng kịp nói họ có file. Cờ được suy MỘT LẦN lúc ghi lượt —
    /// cùng khuôn với <c>readinessVerified</c>, và cùng lý do: suy từ bản CHỐT nên nó không thể vênh với thứ
    /// được lưu. Phạm vi xét vẫn là cửa sổ hội thoại như trước.
    /// </para>
    /// </summary>
    private static void ApplySourceRequestTurn(BAChatTurnDraft draft, TurnContext turn, TurnJudgeVerdict verdict)
    {
        if (SourceRequestStillPossible(turn)
            && verdict.UserMentionsExistingSource
            && !draft.AsksForFile
            && !draft.CarriesTable)
        {
            draft.Replace(SourceRequestTurn.Message, openEnded: true);
        }
    }

    /// <summary>
    /// Lượt xin file còn cửa để bắn không — phần TẤT ĐỊNH của điều kiện (dự án chưa có tài liệu nguồn, có
    /// lượt người dùng để xét, và chưa có lời xin đích danh nào). Tách riêng vì lượt trọng tài cũng hỏi đúng
    /// câu này để biết có đáng gọi model chỉ vì nó hay không.
    /// </summary>
    private static bool SourceRequestStillPossible(TurnContext turn)
        => turn.Sources.Count == 0
           && turn.LastUserIndex >= 0
           && !SourceRequestTurn.AnsweredMention(turn.Recent);

    /// <summary>
    /// Lượt model vừa viết là ĐÚNG lời xin file mà <see cref="ApplySourceRequestTurn"/> đang chờ đặt vào:
    /// người dùng vừa nhắc tới một nguồn dữ liệu đang có, đường xin file còn cửa, và model tự xin rồi.
    /// </summary>
    private static bool IsDueFileRequest(BAChatTurnDraft draft, TurnContext turn, TurnJudgeVerdict verdict)
        => draft.AsksForFile && verdict.UserMentionsExistingSource && SourceRequestStillPossible(turn);

    /// <summary>
    /// NHỊP TÓM TẮT KIỂM CHỨNG mà quên chip — nhận ra qua cờ <c>summaryCheck</c> do CHÍNH MODEL khai
    /// (<see cref="Contracts.Requirements.BAChatReply.SummaryCheck"/>), không phải qua một bảng cụm từ.
    /// Prompt kê sẵn bộ hai chip cho lượt này (["Đúng rồi, tiếp tục",
    /// "Tôi muốn sửa lại"]) vì nó là câu ĐÓNG: người dùng chỉ cần gật hoặc đòi sửa. Thiếu chip thì họ phải
    /// gõ tay một câu xác nhận, và ca thật (JD Libary 5, lượt 20) cho thấy cái giá thật nằm ở chỗ khác:
    /// không có hai nhánh bày sẵn, model tự viết ra một câu hỏi độ ĐẦY ĐỦ ("anh/chị thấy đã đầy đủ chưa?")
    /// và nhận về "đầy đủ rồi" — một lời tuyên bố hoàn tất trong khi bản đồ còn hai nhóm [CHƯA HỎI]. Cùng
    /// luật với chip dự phòng của lượt kể lại file: lượt nào là câu đóng thì phải có nút để bấm.
    /// </summary>
    private static void ApplySummaryCheckChips(BAChatTurnDraft draft)
    {
        if (string.IsNullOrEmpty(draft.SuggestionsJson)
            && draft.Questions.Count == 0
            && !draft.CarriesTable
            && draft.SummaryCheck)
        {
            draft.SetFallbackSuggestions(SummaryCheckSuggestions);
        }
    }

    /// <summary>Lưu lượt assistant vừa nắn xong rồi trả bản CHỐT cho endpoint streaming render tại chỗ.</summary>
    private async Task<BAChatTurnResult> SaveTurnAsync(TurnContext turn, Agent ba, BAChatTurnDraft draft, CancellationToken cancellationToken)
    {
        var project = turn.Project;
        var questionsJson = draft.Questions.Count > 0 ? JsonSerializer.Serialize(draft.Questions) : null;
        var flowMapJson = draft.FlowMap.Count > 0 ? FlowMapBuilder.Serialize(draft.FlowMap) : null;
        // ĐÓNG DẤU "cổng readiness đã pass ở lượt này" lên chính lượt sắp lưu — thứ mà bước soạn tài liệu
        // đọc để biết có được bỏ qua lần xét lại hay không (xem AgentConversation.ReadinessVerified).
        //
        // Suy từ bản CHỐT (`draft.Reply`) chứ không từ nhánh nào phía trên, và đó là điểm mấu chốt: nội
        // dung lượt còn bị nhiều chốt chặn sau đó viết lại — lượt có BẢNG thay lời mời bằng câu dẫn của
        // bảng (TakeOverForTable), lượt câm bị thay bằng bước kế tất định (có thể LÀ lời mời khi bản đồ đã
        // đủ). Đặt cờ ở nhánh "lời mời được giữ" rồi mang xuống đây là dựng lại đúng kiểu vênh mà cột này
        // sinh ra để dẹp: cờ nói một đằng, lượt được lưu một nẻo. Bản đồ dùng để xét là bản đã gộp ở ĐẦU
        // lượt này — cùng dữ liệu mà cổng readiness đã xét, nên hai chỗ không thể lệch nhau.
        var readinessVerified = RequirementReadinessGate.IsReadinessVerifiedTurn(draft.InvitesWriteRequirement, project);
        // ĐÓNG DẤU "lượt này là lời xin tài liệu nguồn" — cùng khuôn và cùng lý do với cờ ngay trên: suy từ
        // bản CHỐT, một lần, rồi mọi lượt sau chỉ đọc cột (xem AgentConversation.SourceRequested). Lượt xin
        // file có thể do CHÍNH MODEL tự viết chứ không chỉ do cơ chế thay vào — ca đó do lượt trọng tài
        // phán (BAChatTurnDraft.AsksForFile), không còn dò chữ.
        var sourceRequested = draft.AsksForFile;

        // CẶP HỎI–ĐÁP NGOÀI PHẠM VI được đóng dấu trên CẢ HAI lượt (xem AgentConversation.OffTopic). Lượt
        // user nhận giá trị của chính câu trả lời này — đặt LẠI ở mọi lượt chứ không chỉ bật lên, vì đường
        // sửa / soạn lại chạy lại đúng lượt user đó: câu sửa có thể đã thành một câu về dự án. Lưu cùng một
        // SaveChanges với lượt BA bên dưới nên hai dấu không bao giờ lệch nhau.
        var answeredUserTurn = await _db.AgentConversations
            .Where(c => c.ProjectId == turn.ProjectId && c.Role == "user")
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (answeredUserTurn != null)
        {
            answeredUserTurn.OffTopic = draft.OffTopic;
            // Cùng khuôn: phán quyết "lượt user này nhắc tới một nguồn dữ liệu đang có" chỉ đúng ở lượt nó
            // được đưa ra, nên được đóng dấu ngay lên lượt user — suất xin file đích danh đọc cặp (lượt user
            // nhắc nguồn → lượt BA xin file) ở mọi lượt sau. Xem SourceRequestTurn.AnsweredMention.
            answeredUserTurn.MentionsSource = draft.UserMentionsSource;
        }

        await _conversationLog.AppendAsync(turn.ProjectId, ba.Id, "assistant", draft.Reply, draft.SuggestionsJson, draft.SuggestionsMultiSelect, questionsJson: questionsJson, flowMapJson: flowMapJson, readinessVerified: readinessVerified, sourceRequested: sourceRequested, offTopic: draft.OffTopic, cancellationToken: cancellationToken);

        // Trả bản CHỐT (đúng bản vừa lưu) để endpoint streaming render tại chỗ — bản preview đã stream
        // có thể khác (vd lời mời bị gate thay bằng câu hỏi), client luôn thay preview bằng bản này.
        return new BAChatTurnResult
        {
            Status = ChatWithBAResult.Ok,
            Reply = draft.Reply,
            Suggestions = string.IsNullOrEmpty(draft.SuggestionsJson)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(draft.SuggestionsJson) ?? new List<string>(),
            InvitesWriteRequirement = draft.InvitesWriteRequirement,
            OffTopic = draft.OffTopic,
            SuggestionsMultiSelect = draft.SuggestionsMultiSelect,
            OpenEnded = draft.OpenEnded,
            Questions = draft.Questions,
            // Bản đồ ở thời điểm này đã gộp tới lượt user mới nhất (cập nhật đầu lượt); lượt BA vừa trả
            // lời sẽ được gộp ở lượt sau — đủ tươi cho panel tiến độ.
            Coverage = CoverageMapParser.AttachQuestions(
                CoverageMapParser.Parse(project.RequirementCoverageMap), turn.CoverageUpdate.Questions).ToList(),
            FlowMap = draft.FlowMap,
            // Bản đồ KHÔNG gộp được lượt này (đã thử lại): panel tiến độ đang hiển thị bản cũ và BA vừa
            // dẫn lượt bằng bản cũ đó. Nói thẳng ra thay vì để người dùng tự đoán vì sao tiến độ đứng im.
            CoverageStale = turn.CoverageUpdate.DistillFailed
        };
    }

    /// <summary>
    /// Đính một khối "bảng … ĐÃ CHỐT" vào ngữ cảnh lượt chat. Không có khối này thì bảng chỉ là một màn bấm
    /// đẹp: BA vẫn hỏi lại đúng thứ người dùng vừa duyệt từng dòng, và mọi tầng phía sau vẫn phải tự đoán.
    /// Chưa chốt (<paramref name="block"/> null/rỗng) ⇒ không đính gì, lượt chạy đúng như trước.
    /// </summary>
    private static void AppendConfirmedTable(List<ChatMessage> messages, string? block, string instruction)
    {
        if (string.IsNullOrWhiteSpace(block))
            return;

        messages.Add(new ChatMessage(ChatRole.System, instruction + "\n" + block));
    }

    /// <summary>
    /// Bước kế tiếp TẤT ĐỊNH khi mọi câu hỏi của lượt vừa rồi đều là câu đã hỏi: hỏi đúng nhóm mà bản đồ
    /// bao phủ còn ghi thiếu, hoặc — khi bản đồ đã đủ theo cùng cổng readiness dùng ở mọi nơi khác — mời
    /// bấm "Write Requirement". Không bao giờ trả về lượt rỗng: một lượt câm sau khi người dùng vừa trả
    /// lời còn khó hiểu hơn cả việc bị hỏi lại.
    /// </summary>
    private static (string Message, bool OpenEnded, bool Invites) BuildFollowUpAfterRepeat(
        Project project,
        IReadOnlyList<OpenQuestionEntry> openQuestions,
        IReadOnlyList<AgentConversation> turns,
        string? blockedQuestion = null)
    {
        // Đường này là chỗ câu chặn của cổng dễ lặp nhất: lượt của BA toàn câu đã hỏi thì lượt nào cũng rơi
        // vào đây, và nếu bản đồ chưa nhúc nhích thì cổng lại chọn đúng nhóm cũ. `turns` cho cổng đổi nhóm.
        //
        // `blockedQuestion` là câu BA vừa bị chặn — cổng dùng nó để phá thế hoà giữa các nhóm ĐỀU chưa
        // được hỏi, chọn nhóm gần chủ đề đang bàn dở nhất. Nó KHÔNG nới luật xoay vòng: câu vừa phát vẫn
        // lùi lại một vòng như cũ. Xem RequirementReadinessGate.TopicOverlap.
        var readiness = RequirementReadinessGate.Evaluate(project, openQuestions, turns, blockedQuestion);
        if (!readiness.Ready)
        {
            return (string.IsNullOrWhiteSpace(readiness.Message)
                ? Loc.T("BAChatService.BuildFollowUpAfterRepeat")
                : readiness.Message, readiness.OpenEnded, false);
        }

        // Bản đồ đã đủ ⇒ lời mời này đi qua đúng cổng mà nhánh dưới sẽ xét lại, nên không thể là lời mời
        // sớm. Không phải câu hỏi nên cũng không mở ô nhập: hành động duy nhất lúc này là bấm nút thật.
        return (Loc.T("BAChatService.BuildFollowUpAfterRepeat.2"),
            false, true);
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
            var sourceContents = _sourceContextBuilder.Build(sources, model);
            if (sourceContents.Count == 0)
            {
                // Không có gì đọc được (model không vision với ảnh / PDF scan). Nếu người dùng vừa gửi từ
                // khung chat thì nói rõ lý do thay vì im lặng — họ còn đường khác (gõ tóm tắt nội dung ảnh).
                if (!string.IsNullOrEmpty(trimmedNote) || attachmentsJson != null)
                    await _conversationLog.AppendAsync(projectId, ba.Id, "assistant",
                        Loc.T("BAChatService.AcknowledgeSourcesAsync"),
                        cancellationToken: cancellationToken);
                return false;
            }

            var scope = BuildSourceAckScope(sources, attachments);

            // Ghi chú người dùng gõ cạnh ảnh (nếu có) → BA đọc đúng trọng tâm thay vì tóm tắt chung chung.
            // Câu này GỌI TÊN đúng các file vừa gửi: câu cũ ("đây là các tài liệu nguồn tôi vừa đính kèm")
            // đứng trước text của mọi nguồn nên nó khai man rằng file cũ cũng vừa được gửi.
            var justSentList = string.Join(", ", scope.JustSentFiles);
            var promptText = string.IsNullOrEmpty(trimmedNote)
                ? Loc.T("BAChatService.AcknowledgeSourcesAsync.2", justSentList)
                : Loc.T("BAChatService.AcknowledgeSourcesAsync.3", justSentList, trimmedNote);

            var userContent = new List<AIContent> { new TextContent(promptText) };
            userContent.AddRange(sourceContents.Contents);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/source-ack.v3.md")),
                new(ChatRole.System, BASourceAckPrompt.ColumnScope(scope.PendingColumnFiles)
                    + "\n\n" + BASourceAckPrompt.ReadbackScope(scope.JustSentFiles, scope.EarlierFiles))
            };

            // Bối cảnh hội thoại: không có nó thì "đối chiếu file với điều người dùng đã kể" là một lệnh
            // không thi hành nổi — xem BASourceAckPrompt.ConversationContext.
            var conversation = await LoadAckConversationContextAsync(project, cancellationToken);
            if (conversation.Length > 0)
                messages.Add(new ChatMessage(ChatRole.System, conversation));

            messages.Add(new ChatMessage(ChatRole.User, userContent));

            var (callResult, parsed, callError) = await CallSourceAckAsync(projectId, ba, model, messages, cancellationToken);

            var reply = callResult is { IsSuccess: true } ? (parsed ?? _replyParser.Parse(callResult.Content)) : null;
            // Lượt phải gửi lại mà bỏ ảnh có kèm một dòng dặn dò NỘI BỘ cho model; model yếu hay chép nguyên
            // văn dòng đó vào câu trả lời. Dọn TRƯỚC vòng kiểm tra rỗng bên dưới: một lượt mà nội dung chỉ
            // gồm đúng dòng dặn dò ấy là lượt model chưa nói gì của riêng nó, phải rơi vào nhánh ⚠️ (có nút
            // "Thử lại") chứ không phải hiện lên thành một bong bóng trống.
            if (reply != null)
                reply.Message = EndpointQuirks.StripInternalNotices(reply.Message);
            if (reply == null || string.IsNullOrWhiteSpace(reply.Message))
            {
                // Trước đây chỗ này return false im lặng: ghi chú của user đã hiện trong hội thoại nhưng BA
                // "mất tích". Lưu lượt ⚠️ như RunTurnAsync để UI tô đỏ + nút "Thử lại" (retry xóa lượt lỗi
                // rồi chạy lại lượt chat thường — tài liệu nguồn vẫn được đính vào lượt user mới nhất).
                var detail = callError ?? callResult?.ErrorMessage ?? callResult?.Content;
                await _conversationLog.AppendAsync(projectId, ba.Id, "assistant",
                    Loc.T("BAChatService.AcknowledgeSourcesAsync.4", RequirementScreenText.LlmFailurePrefix, detail),
                    cancellationToken: cancellationToken);
                return false;
            }

            // BẢNG CỘT cho các nguồn dạng bảng tính (xem SourceColumnMapBuilder). Structured output tắt ⇒
            // parsed null ⇒ thử đọc lại từ raw content, đúng như phần SourceNotes bên dưới.
            var proposedColumns = parsed?.Columns
                ?? LlmJson.TryDeserialize<BASourceAckReply>(callResult?.Content, requireKnownProperty: true)?.Columns;
            var columnRows = BuildColumnMapRows(sources, proposedColumns);

            // LƯU LUÔN phạm vi cột BA vừa chốt vào chính file nguồn — người dùng không phải rà hay gửi bảng.
            // Đây là thứ RealSampleDataReader lọc dữ liệu mẫu theo, SourceContextBuilder đính vào mọi lượt
            // sau và bản đồ bao phủ coi là phần "bộ cột" đã xong. Bảng vẫn được ghi lên lượt assistant
            // (cột ColumnMap) để bong bóng của lượt này mở ra "Xem / sửa bảng cột" được.
            await SaveColumnMapsAsync(projectId, columnRows, cancellationToken);
            var columnMapJson = columnRows.Count > 0 ? JsonSerializer.Serialize(columnRows) : null;

            // Chip của lượt này là phương án trả lời CÂU HỎI KẾT (prompt bắt kết bằng đúng một câu hỏi khai
            // thác) — không còn là cặp "Đúng rồi / Chưa đúng" xin duyệt bản đọc.
            var suggestionsJson = reply.Suggestions.Count > 0
                ? JsonSerializer.Serialize(reply.Suggestions)
                : null;
            await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", reply.Message.Trim(), suggestionsJson,
                reply.MultiSelect, columnMapJson: columnMapJson, cancellationToken: cancellationToken);

            // Đây là lượt DUY NHẤT model nhìn thấy ảnh. Cất phần nó ghi lại được về từng hình để các lượt
            // chat sau dùng chữ thay ảnh. Structured output tắt ⇒ parsed null ⇒ thử đọc lại từ raw content
            // (model vẫn hay trả đúng JSON dù không được ép); vẫn không có ⇒ không cất gì, ảnh tiếp tục đi
            // kèm như trước — tốn token nhưng không mất nội dung, đó mới là thứ không được phép hỏng.
            var notes = parsed?.SourceNotes
                ?? LlmJson.TryDeserialize<BASourceAckReply>(callResult?.Content, requireKnownProperty: true)?.SourceNotes;
            // Lượt vừa rồi rốt cuộc đi ra KHÔNG kèm ảnh (endpoint chặn content ảnh, hoặc body quá lớn nên
            // LlmClient gửi lại bản text) ⇒ model chưa hề nhìn thấy tấm nào, mọi "mô tả hình" nó viết ra là
            // bịa. Khóa bản bịa đó vào VisionSummary là mất VĨNH VIỄN đường nhìn lại ảnh — thà để nguyên,
            // lượt sau ảnh vẫn được ưu tiên hạn mức.
            var attachedIds = callResult is { ImagesDropped: true }
                ? Array.Empty<Guid>()
                : sourceContents.FullyAttachedSourceIds;
            await StoreVisionSummariesAsync(attachedIds, sources, notes, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (userTurnAppended && baId.HasValue)
                await TryCloseTurnWithFailureAsync(projectId, baId.Value, new OperationCanceledException(Loc.T("BAChatService.AcknowledgeSourcesAsync.5")));
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
    /// Ba nhóm file của một lượt đọc tài liệu: file lượt này phải KỂ LẠI, bảng tính còn CHỜ CHỐT CỘT, và
    /// nguồn CŨ chỉ đính kèm để đối chiếu. Xem <see cref="BuildSourceAckScope"/> cho lý do phải tách.
    /// </summary>
    private sealed record SourceAckScope(
        List<string> JustSentFiles,
        List<string> PendingColumnFiles,
        List<string> EarlierFiles);

    /// <summary>
    /// PHẠM VI của lượt đọc file: file VỪA GỬI, tách khỏi các nguồn cũ. Lượt này nạp lại TOÀN BỘ nguồn của
    /// project và điều đó là cố ý — nguồn cũ là thứ duy nhất để ĐỐI CHIẾU, mà chỗ nối giữa file mới và file
    /// cũ thường là điểm chưa rõ đắt nhất của cả buổi. Nhưng "đính kèm để đối chiếu" khác hẳn "phải kể
    /// lại": không tách hai việc đó ra thì model — vốn thấy mọi nguồn nằm dưới cùng một câu "tôi vừa đính
    /// kèm" — kể lại cả những file người dùng đã xác nhận từ lượt trước, và họ phải đọc lại lần thứ hai
    /// đúng thứ mình vừa duyệt trước khi tới được phần nói về file vừa gửi.
    /// </summary>
    private static SourceAckScope BuildSourceAckScope(
        List<ProjectSourceFile> sources, IReadOnlyList<ChatAttachment>? attachments)
    {
        var justSentIds = attachments?.Select(a => a.Id).ToHashSet() ?? new HashSet<Guid>();
        var justSentFiles = sources.Where(s => justSentIds.Contains(s.Id)).Select(s => s.FileName).ToList();
        // Không biết lô nào vừa gửi (caller không truyền danh sách) ⇒ giữ nguyên hành vi cũ: coi mọi
        // nguồn là vừa gửi. Đoán bừa một tập con là nguy hiểm hơn hẳn việc kể lại thừa — file vừa gửi
        // mà rơi khỏi phạm vi kể lại thì lượt bắt lỗi đầu vào của chính nó biến mất.
        var scopedToNewFiles = justSentFiles.Count > 0;
        if (!scopedToNewFiles)
            justSentFiles = sources.Select(s => s.FileName).ToList();

        // File nào cần chốt bảng cột do CƠ CHẾ nói ra, không để model tự đoán: model nhìn thấy text của
        // MỌI nguồn trong project (kể cả file đã có bảng cột từ lần upload trước) nên nó không tự suy ra
        // được file nào đang chờ.
        var pendingColumnFiles = sources
            .Where(s => s.Kind == SourceFileKind.Spreadsheet && s.ExtractedText != null && s.ColumnMap == null)
            .Select(s => s.FileName)
            .ToList();

        // Nguồn CŨ mà lượt này không còn việc gì với nó. Bảng tính cũ chưa chốt cột KHÔNG nằm ở đây dù
        // nó cũng là nguồn cũ: bảng của nó được chốt ngay lượt này (BuildColumnMapRows lấy mọi file
        // ColumnMap == null), nên câu phạm vi cột của lượt vẫn phải được phép gọi tên nó.
        var earlierFiles = scopedToNewFiles
            ? sources
                .Where(s => !justSentIds.Contains(s.Id) && !pendingColumnFiles.Contains(s.FileName))
                .Select(s => s.FileName)
                .ToList()
            : new List<string>();

        return new SourceAckScope(justSentFiles, pendingColumnFiles, earlierFiles);
    }

    /// <summary>
    /// Lời gọi LLM của lượt đọc file. Nó có thể THROW trước cả khi tới model (ví dụ ApiKey rỗng làm
    /// <c>BuildClient</c> ném <see cref="ArgumentException"/>) chứ không chỉ trả <c>IsSuccess=false</c> —
    /// bắt riêng tại đây để lỗi nào cũng thành lượt ⚠️ hiển thị được, thay vì lọt xuống catch-all của
    /// <see cref="AcknowledgeSourcesAsync"/> và BA "mất tích" không dấu vết.
    /// </summary>
    private async Task<(LlmCallResult? CallResult, BASourceAckReply? Parsed, string? Error)> CallSourceAckAsync(
        Guid projectId, Agent ba, AiModel model, List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        try
        {
            var (callResult, parsed) = await _llm.ChatStructuredAsync<BASourceAckReply>(
                model, messages, new ModelCallLogContext(projectId, ba, "BASourceAck"),
                cancellationToken: cancellationToken);
            return (callResult, parsed, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message);
        }
    }

    /// <summary>
    /// Dựng bảng cột cho các nguồn BẢNG TÍNH chưa có bảng của project từ đề xuất của model. Rỗng khi không có
    /// gì để chốt — khi đó không có bảng nào được lưu và dữ liệu mẫu không bị lọc (đúng hành vi trước khi có
    /// bảng cột).
    ///
    /// <para>
    /// Bỏ qua file ĐÃ có bảng cột (<see cref="ProjectSourceFile.ColumnMap"/> khác null): lượt đọc file nạp
    /// lại TOÀN BỘ nguồn của project, nên không có bộ lọc này thì mỗi lần người dùng đính thêm một file mới
    /// là bảng của các file trước bị dựng — và ghi đè — lại theo một lời gọi model khác.
    /// </para>
    /// </summary>
    private static List<SourceColumnNote> BuildColumnMapRows(List<ProjectSourceFile> sources, List<SourceColumnNote>? proposed)
    {
        var rows = new List<SourceColumnNote>();
        if (proposed is not { Count: > 0 })
            return rows;

        var pending = sources
            .Where(s => s.Kind == SourceFileKind.Spreadsheet && s.ExtractedText != null && s.ColumnMap == null)
            .ToList();
        if (pending.Count == 0)
            return rows;

        // Chỉ có MỘT bảng tính đang chờ ⇒ mọi dòng đều thuộc về nó, bất kể model ghi fileName thế nào.
        // Không có nhánh này thì cả bảng bị vứt vì một chi tiết hình thức: model bỏ trống trường này khi
        // lượt chỉ có một file, hoặc chép tên "đẹp" mà người dùng gọi thay vì tên đã lưu (file upload được
        // gắn tiền tố chống trùng, nên "LearningPlan.xlsx" và "74a9af7d-LearningPlan.xlsx" là chuyện thường).
        // Nhiều file thì KHÔNG đoán: gán nhầm bảng còn tệ hơn không có bảng.
        if (pending.Count == 1)
        {
            foreach (var note in proposed.Where(n => n != null))
                note.FileName = pending[0].FileName;
        }

        foreach (var source in pending)
            rows.AddRange(SourceColumnMapBuilder.Build(source.FileName, proposed, SourceColumnMapBuilder.ReadColumnNames(source.ExtractedText)));

        return rows;
    }

    /// <summary>
    /// Ghi bảng cột vừa dựng vào <see cref="ProjectSourceFile.ColumnMap"/> của từng file. Danh sách nguồn của
    /// lượt đọc file được nạp AsNoTracking (chở cả ExtractedText dài) nên ở đây nạp lại đúng các file cần ghi.
    /// </summary>
    private async Task SaveColumnMapsAsync(Guid projectId, List<SourceColumnNote> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return;

        var byFile = rows
            .GroupBy(r => r.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var files = await _db.ProjectSourceFiles
            .Where(s => s.ProjectId == projectId && s.Kind == SourceFileKind.Spreadsheet && s.ColumnMap == null)
            .ToListAsync(cancellationToken);
        foreach (var file in files)
            if (byFile.TryGetValue(file.FileName, out var fileRows))
                file.ColumnMap = JsonSerializer.Serialize(fileRows);

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Ngữ cảnh hội thoại cho lượt đọc file: tóm tắt + bản đồ bao phủ ĐÃ LƯU và các lượt chưa gộp vào tóm
    /// tắt. Không gọi LLM — xem <see cref="BASourceAckPrompt.ConversationContext"/>.
    /// </summary>
    private async Task<string> LoadAckConversationContextAsync(Project project, CancellationToken cancellationToken)
    {
        var turns = await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip(project.SummarizedTurnCount)
            .ToListAsync(cancellationToken);

        return BASourceAckPrompt.ConversationContext(project.ConversationSummary, project.RequirementCoverageMap, turns);
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

        // Chốt trước xem nguồn nào THẬT SỰ có ghi chú để ghi, rồi mới đụng DB: lượt đính ảnh nào cũng có
        // nguồn không được model ghi chú, và một vòng lặp hỏi DB từng id sẽ tốn đúng số roundtrip bằng số
        // nguồn đã đính (N+1) cho những dòng phần lớn bị bỏ qua ngay sau đó.
        var pending = new Dictionary<Guid, string>();
        foreach (var source in sources)
        {
            if (fullyAttachedIds.Contains(source.Id)
                && byName.TryGetValue(source.FileName.Trim(), out var summary))
                pending[source.Id] = summary;
        }

        if (pending.Count == 0)
            return;

        // sources đọc bằng AsNoTracking ⇒ cập nhật qua entity đang track, không attach bản no-tracking
        // (sẽ đụng cả ExtractedText dài). Một truy vấn cho cả lô thay vì từng id một.
        var pendingIds = pending.Keys.ToList();
        var tracked = await _db.ProjectSourceFiles
            .Where(s => pendingIds.Contains(s.Id))
            .ToListAsync(cancellationToken);

        var updated = 0;
        foreach (var row in tracked)
        {
            if (!string.IsNullOrWhiteSpace(row.VisionSummary))
                continue;

            row.VisionSummary = pending[row.Id];
            updated++;
        }

        if (updated > 0)
            await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Kết quả 3 bước chuẩn bị ngữ cảnh của một lượt chat.</summary>
    /// <param name="foldThroughTurn">Mốc mà bộ nhớ hội thoại phải gộp hết phần đứng trước (0 = không ép) — xem
    /// <see cref="ConversationMemoryService.LoadAsync"/>.</param>
    private async Task<(ConversationMemoryService.Memory Memory, string? UserMemory, RequirementCoverageService.CoverageUpdate Coverage)> PrepareTurnContextAsync(
        Project project, Agent ba, AiModel model, int foldThroughTurn, CancellationToken cancellationToken)
    {
        // Không có scope factory (unit test dựng tay) ⇒ tuần tự trên scope hiện tại — hành vi cũ.
        if (_scopeFactory == null)
        {
            var seqMemory = await _memory.LoadAsync(project, ba, model, cancellationToken, foldThroughTurn);
            var seqUserMemory = await _userMemory.UpdateAndLoadAsync(project, ba, model, cancellationToken);
            var seqCoverage = await _coverage.UpdateAndLoadAsync(project, ba, model, cancellationToken);
            return (seqMemory, seqUserMemory, seqCoverage);
        }

        // DbContext không thread-safe nên mỗi nhánh chạy trong MỘT DI scope riêng: tự load Project/BA
        // từ context của scope đó rồi gọi đúng service tương ứng. Ba nhánh ghi các CỘT KHÁC NHAU trên
        // dòng Projects (summary / con trỏ user-memory / bản đồ bao phủ) nên không giẫm nhau.
        var memoryTask = RunInScopeAsync((sp, prj, agent) =>
            sp.GetRequiredService<ConversationMemoryService>().LoadAsync(prj, agent, agent.AiModel!, cancellationToken, foldThroughTurn), project.Id, cancellationToken);
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

    // Encoder GIỮ NGUYÊN chữ có dấu — không phải chuyện thẩm mỹ mà là chuyện TIỀN và chuyện ĐỌC ĐƯỢC.
    // Mặc định của JsonSerializer escape mọi ký tự non-ASCII, nên một lượt BA tiếng Việt đi lên model
    // dưới dạng "C\u1EA3m \u01A1n anh/ch\u1ECB…": dài gấp ~1,8 lần và tốn gấp mấy lần token, vì mỗi
    // \uXXXX là một chuỗi 6 ký tự ASCII vô nghĩa với tokenizer. Đo trên một lượt thật (log BAChat
    // 2026-09-05, 16 lượt assistant): 12.430 ký tự escape so với 6.830 ký tự thật — 5.600 ký tự trả
    // tiền cho không. Và transcript là chỗ DUY NHẤT model đọc lại các câu nó đã hỏi (xem
    // <see cref="AskedQuestionHistory"/>), nên khó đọc ở đây là hỏng thẳng vào phanh chống hỏi lại.
    // Cùng lựa chọn với CoverageMapParser / InterviewOutlookParser.
    private static readonly JsonSerializerOptions AssistantContextJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Dựng lại một lượt BA cũ theo đúng JSON shape mà model được yêu cầu xuất, để củng cố format ở
    // mỗi lượt. Không có việc này, model nhìn các lượt trước là văn xuôi và sẽ bỏ JSON (kèm gợi ý) từ
    // lượt thứ 2. Suggestions hỏng/cũ thì coi như mảng rỗng.
    private static string BuildAssistantContext(AgentConversation c)
    {
        // Lượt đáp câu NGOÀI PHẠM VI echo lại đúng hình dạng model đã khai (`message` rỗng + cờ), không echo
        // câu chữ cơ chế dựng: echo câu từ chối + câu hỏi chép lại là dạy model tự viết lượt này, trong khi
        // prompt bắt nó để rỗng. Câu hỏi được chép lại vẫn có mặt ở lượt BA gốc ngay phía trên.
        if (c.OffTopic)
            return JsonSerializer.Serialize(new { message = "", offTopic = true }, AssistantContextJson);

        // Parse chung với đường render transcript (ConversationTurnRenderer): null/rỗng/hỏng → mảng rỗng.
        var suggestions = ConversationTurnRenderer.ParseSuggestions(c.Suggestions);

        // "ready" echo lại ĐÚNG cờ mà lượt đó đã được lưu với (AgentConversation.ReadinessVerified: model
        // khai `ready` VÀ cổng readiness cho đứng). Trước đây nó được suy ngược từ mặt chữ của lượt, nên
        // model học lại một cờ không phải cờ nó đã khai — dạy sai chính cái trường mà nay cổng đọc.
        var ready = c.ReadinessVerified;
        // KHÔNG echo sơ đồ luồng nữa: trường đã ra khỏi schema trả lời (xem BAChatReply), nên dựng lại một
        // lượt cũ có nó là dạy model đúng cái format vừa gỡ. Luồng mà người dùng đã duyệt quay lại ngữ cảnh
        // ở khối "bảng đã chốt" (FlowMapBuilder.RenderConfirmedBlock), đầy đủ hơn hẳn.
        // Echo cả các câu hỏi của lượt GỘP: đây là chỗ model học rằng gộp là hợp lệ VÀ học nhịp gộp của
        // chính nó. Bỏ trường này thì mọi lượt cũ trông như lượt một-câu và model trượt về một-câu-một-lượt
        // sau vài vòng — đúng kiểu trượt format mà hàm này sinh ra để chặn.
        var questions = ConversationTurnRenderer.ParseQuestions(c.Questions)
            .Select(q => new { group = q.Group, question = q.Question, suggestions = q.Suggestions, multiSelect = q.MultiSelect, openEnded = q.OpenEnded });
        return JsonSerializer.Serialize(
            new { message = c.Message, suggestions, multiSelect = c.SuggestionsMultiSelect, questions, ready },
            AssistantContextJson);
    }
}
