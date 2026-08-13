using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
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

    /// <summary>
    /// Câu nói thêm khi lượt đọc file LẼ RA có bảng cột mà rốt cuộc không dựng được (model không trả
    /// <c>columns</c> dùng được). Không có nó, <c>message</c> của lượt — vốn đã được viết theo hình dạng
    /// "mời anh/chị rà bảng bên dưới" — trỏ vào một cái bảng không tồn tại, và người dùng đi tìm một cái
    /// nút không có trên màn hình. Đúng lỗi "câu hỏi không có nút trả lời", chỉ khác chiều.
    /// </summary>
    public const string ColumnMapMissingNotice =
        "\n\n(Mình chưa dựng được bảng cột cho file bảng tính vừa gửi. Anh/chị gõ giúp mình các cột thật sự "
        + "dùng khi làm việc nhé — mình sẽ chỉ dựng ứng dụng trên đúng những cột đó.)";

    /// <summary>
    /// Hai chip dự phòng của lượt KỂ LẠI file bảng tính (sau khi người dùng chốt bảng cột): lượt đó kết
    /// bằng một câu hỏi đóng nên phải có đúng hai đáp án để bấm — một xác nhận, một đính chính.
    /// </summary>
    public static readonly IReadOnlyList<string> SourceReadbackSuggestions =
        new[] { "Đúng rồi", "Có chỗ chưa đúng" };

    /// <summary>
    /// Câu dẫn dự phòng cho lượt bày bảng phân quyền, dùng khi model không viết được câu dẫn dùng được.
    /// Nó phải CHỈ VÀO BẢNG chứ không kết bằng một câu hỏi đóng: lượt này không có chip, nên một câu hỏi
    /// ở đây là câu hỏi KHÔNG CÓ NÚT TRẢ LỜI — người dùng đi tìm nút "Đúng rồi" không thấy trong khi việc
    /// thật sự phải làm nằm ngay dưới. Đúng lỗi mà lượt đọc bảng tính đã vấp một lần.
    /// </summary>
    public const string PermissionMatrixIntro =
        "Các nhóm thông tin khác mình đã ghi nhận đủ, còn lại phần phân quyền. Mình đã dựng sẵn bảng bên dưới "
        + "theo các màn hình đã chốt: anh/chị chọn phạm vi cho từng vai trò (ô để trống là vai đó không có quyền), "
        + "rồi bấm \"Gửi bảng phân quyền\" giúp mình nhé.";

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

        // LƯỢT KỂ LẠI FILE BẢNG TÍNH: người dùng vừa gửi bảng cột đã tích, và tin nhắn đó do server soạn
        // nên nhận ra được chắc chắn (SourceColumnMapBuilder.IsSubmissionMessage). Bản đọc lại của bảng
        // tính bị cố ý dời từ lượt upload xuống đây — xem BuildSourceAckTurnShape. Không có lượt này thì
        // chốt cột xong là BA hỏi thẳng câu tiếp theo, và cái sai duy nhất còn lại ở đầu vào (BA hiểu file
        // kể chuyện gì) không bao giờ được người dùng nhìn thấy để bác.
        var columnReadbackTurn = lastUserIndex >= 0
            && SourceColumnMapBuilder.IsSubmissionMessage(recent[lastUserIndex].Message)
            && sources.Any(s => s.Kind == SourceFileKind.Spreadsheet && !string.IsNullOrWhiteSpace(s.ColumnMap));

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
        // "Điều đã chốt" (DecisionLogService): các quyết định người dùng đã nói/đã xác nhận, gộp lũy tiến
        // qua MỌI lượt. Trước đây nhật ký này chỉ hiện thành panel cạnh khung chat để người dùng tự canh —
        // một nghịch lý, vì BA mới là bên đọc được cả hội thoại, còn người dùng thì phải vừa kể chuyện vừa
        // đối chiếu một danh sách 40 dòng. Nay nó đi thẳng vào ngữ cảnh của BA (panel đã gỡ) để BA soát
        // mâu thuẫn NGAY trong lượt, thay vì dồn hết cho cổng soát trước lúc soạn tài liệu — lúc đó người
        // dùng phải chọn A/B cho một câu họ nói từ lượt 3, đã nguội hẳn bối cảnh.
        //
        // Vì sao không để BA tự đọc lại transcript: các lượt cũ bị ConversationMemoryService NÉN thành bản
        // tóm tắt để tiết kiệm token, nên chi tiết đã chốt bị bào mòn đúng ở hội thoại dài — cũng chính là
        // lúc mâu thuẫn dễ xảy ra nhất. Nhật ký là bản đúc kết duy nhất sống sót qua việc nén đó.
        var settledDecisions = DecisionLogService.ParseItems(project.DecisionLog);
        if (settledDecisions.Count > 0)
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## Điều đã chốt (các quyết định người dùng ĐÃ nói/đã xác nhận — đối chiếu trước khi hỏi tiếp)\n"
                + "TRƯỚC khi soạn câu hỏi kế tiếp, đối chiếu câu người dùng VỪA trả lời với danh sách này. "
                + "Nếu nó CHỌI với một điều đã chốt, lượt này PHẢI là lượt gỡ mâu thuẫn (xem mục \"Soát mâu "
                + "thuẫn với điều đã chốt\" trong hướng dẫn), KHÔNG hỏi sang nhóm khác. Không chọi nhau thì "
                + "coi đây là điều đã biết: KHÔNG hỏi lại, KHÔNG bắt người dùng xác nhận lần nữa.\n"
                + string.Join("\n", settledDecisions.Select(d => "- " + d))));
        }
        // "Điểm cần làm rõ" (InterviewOutlookService.OpenQuestions): tồn đọng các điểm còn mơ hồ/mâu thuẫn
        // chắt từ hội thoại. Bản đồ ở trên chỉ có độ phân giải theo NHÓM ("Quy tắc nghiệp vụ: MỘT PHẦN"),
        // còn danh sách này giữ ĐÚNG điểm chưa chốt ("Reference Belt đồng bộ tự động hay nhập tay?") —
        // BA mỗi lượt chỉ hỏi 1-2 câu nên phần chưa hỏi tới cần một chỗ để không rơi. Trước đây danh sách
        // này chỉ hiện thành panel cạnh chat để user tự đọc; nay nó đi thẳng vào ngữ cảnh của BA — người
        // dùng chỉ cần trò chuyện, việc "hỏi cho hết" là của BA.
        var openQuestions = InterviewOutlookService.ParseItems(project.OpenQuestions);
        if (openQuestions.Count > 0)
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## Điểm cần làm rõ còn tồn đọng (chắt từ các lượt trước — hỏi cho hết trong khung chat)\n"
                + "Chọn câu hỏi kế tiếp ƯU TIÊN từ danh sách này khi nó còn mục, trước khi mở nhóm mới trong "
                + "bản đồ bao phủ. Điểm nào người dùng đã trả lời ở lượt gần đây thì coi như xong, KHÔNG hỏi lại.\n"
                + string.Join("\n", openQuestions.Select(q => "- " + q))));
        }
        // PHÂN QUYỀN — nhóm DUY NHẤT không được hỏi bằng câu hỏi. Xem PermissionMatrixGate cho lý do đầy
        // đủ; tóm tắt: hỏi "mỗi vai trò được xem và làm những gì?" là bắt người dùng nghiệp vụ tự dựng cả
        // ma trận trong đầu, nên câu trả lời thật gần như luôn là "cứ vậy đã, có gì tôi bổ sung sau" — rồi
        // BA tự soạn phương án và một chip "Đồng ý" đóng dấu [RÕ] cho cả nhóm. Ba trạng thái, ba lệnh khác
        // nhau, và lệnh nào cũng do CƠ CHẾ chọn chứ không để model tự đoán đang ở trạng thái nào.
        var plannedScope = InterviewOutlookService.ParseItems(project.PlannedScope);
        // Lượt kể lại file đã có việc riêng và chỉ có MỘT chỗ trả lời (hai chip xác nhận) — cổng phân quyền
        // nhường một lượt. Hai khối "LƯỢT NÀY" cùng lúc là hai mệnh lệnh chọi nhau, và cổng sẽ mở lại ngay
        // lượt sau.
        var askPermissionMatrix = !columnReadbackTurn && PermissionMatrixGate.ShouldAsk(project);
        var confirmedMatrix = PermissionMatrixBuilder.RenderConfirmedBlock(project.PermissionMatrix);
        if (!string.IsNullOrWhiteSpace(confirmedMatrix))
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## Bảng phân quyền người dùng ĐÃ CHỐT (tự tay chọn từng ô — coi như điều đã biết)\n"
                + "KHÔNG hỏi lại bất kỳ quyền nào dưới đây, KHÔNG bắt xác nhận lần nữa, và KHÔNG dựng yêu cầu "
                + "trái với bảng này.\n"
                + confirmedMatrix));
        }
        else if (askPermissionMatrix)
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## LƯỢT NÀY: BÀY BẢNG PHÂN QUYỀN (bắt buộc)\n"
                + "Mọi nhóm khác của bản đồ bao phủ đã [RÕ]. Lượt này là lượt chốt nhóm «Phân quyền theo "
                + "nghiệp vụ», và nó được chốt bằng BẢNG chứ không bằng câu hỏi.\n"
                + "Trả về trường `permissionMatrix`: mỗi dòng là MỘT chức năng của MỘT màn hình, kèm quyền của "
                + "từng vai trò. Ràng buộc:\n"
                + "- `screen` phải CHÉP ĐÚNG một mục trong danh sách phạm vi bên dưới — không thêm màn hình mới, "
                + "không gộp hai mục làm một. Mục nào bạn không nêu, hệ thống tự bổ sung vào bảng ở trạng thái "
                + "chưa ai có quyền.\n"
                + "- `function`: động từ nghiệp vụ ngắn (\"Xem\", \"Tạo\", \"Sửa\", \"Xóa\", \"Duyệt/Từ chối\", "
                + "\"Cập nhật kết quả\"). Chỉ nêu chức năng màn hình đó THẬT SỰ có theo hội thoại.\n"
                + "- `grants`: mỗi vai trò một mục, `scope` là MỘT trong \"của mình\" / \"của đơn vị\" / \"tất cả\", "
                + "hoặc để rỗng nếu vai đó không có quyền. Phạm vi là phần quan trọng nhất của bảng — "
                + "\"xem Training Plan\" và \"xem Training Plan do mình lập\" là hai yêu cầu khác hẳn nhau.\n"
                + "- `evidence`: CHỈ điền khi người dùng đã tự nói điều đó trong hội thoại, và điền đúng trích dẫn "
                + "của họ. Ô có trích dẫn được khóa lại như điều đã chốt; ô bạn suy đoán thì để trống trường này "
                + "và người dùng sẽ tự chọn. TUYỆT ĐỐI không bịa trích dẫn để ô trông như đã chốt.\n"
                + "- `condition`: điều kiện dữ liệu mà bốn nấc phạm vi không chở nổi (\"chỉ đăng ký được khóa nằm "
                + "trong danh sách bắt buộc của mình\", \"chỉ sửa khi chưa submit\"). Không có thì để rỗng.\n"
                + "`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm \"Gửi bảng phân quyền\" — không đặt "
                + "câu hỏi, không kèm `suggestions`, không kèm `questions`, không kèm `flowDiagram`: bảng là chỗ "
                + "trả lời DUY NHẤT của lượt này.\n\n"
                + "### Phạm vi dự kiến (mỗi mục là MỘT dòng nhóm của bảng — chép nguyên văn vào `screen`)\n"
                + string.Join("\n", plannedScope.Select(s => "- " + s))));
        }
        else
        {
            messages.Add(new ChatMessage(ChatRole.System,
                "## Nhóm «Phân quyền theo nghiệp vụ» — ĐỂ CUỐI, đừng hỏi lẻ\n"
                + "KHÔNG hỏi các câu kiểu \"mỗi vai trò được xem và thao tác những gì\", \"vai X còn được làm gì "
                + "nữa không\", và KHÔNG tự soạn một phương án phân quyền rồi xin người dùng gật. Quyền xem/tạo/"
                + "sửa/xóa theo từng màn hình sẽ được chốt bằng MỘT BẢNG ở cuối buổi, khi phạm vi màn hình đã "
                + "đứng yên — hỏi bây giờ chỉ nhận về \"cứ vậy đã, có gì tôi bổ sung sau\".\n"
                + "Vẫn PHẢI hỏi như thường: vai trò nào làm bước nào trong LUỒNG (ai gửi, ai duyệt, ai bị từ "
                + "chối thì làm gì), vì câu trả lời đó đổi luôn câu hỏi kế tiếp của bạn; và ai QUẢN LÝ từng danh "
                + "mục dữ liệu. Đó là nhóm «Chức năng & luồng nghiệp vụ chính» và «Dữ liệu / danh mục chính», "
                + "không phải nhóm phân quyền."));
        }
        // LƯỢT KỂ LẠI FILE BẢNG TÍNH — nửa sau của cơ chế "bảng cột trước, bản đọc lại sau". Luật viết bản
        // đọc lại nằm trong prompt riêng (đo được ở Prompt Evals, sửa được ở Prompt Studio) chứ không nhét
        // thành chuỗi ở đây; khối này chỉ chọn ĐÚNG lượt để đính nó vào.
        if (columnReadbackTurn)
        {
            messages.Add(new ChatMessage(ChatRole.System,
                _promptTemplateService.Get("BusinessAnalyst/source-readback.v1.md")));
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
        // Lượt hỏi MỘT câu MỞ (xin lời kể): không có chip, UI mời gõ vào ô nhập. Các nhánh TẤT ĐỊNH bên
        // dưới (phanh chống hỏi lại, cổng readiness) đều thay lượt bằng câu hỏi đóng có sẵn phương án,
        // nên chúng phải hạ cờ này — để sót thì màn hình mời "kể tự do" ngay dưới một hàng chip.
        var openEnded = false;
        var questions = new List<BAChatQuestion>();
        var flowDiagram = new List<FlowStep>();
        var permissionMatrix = new List<PermissionMatrixRow>();
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
            // Lượt phải gửi lại mà bỏ ảnh có kèm một dòng dặn dò NỘI BỘ cho model; model yếu hay chép
            // nguyên văn dòng đó vào câu trả lời. Dọn trước khi nó thành một lượt hội thoại.
            reply = string.IsNullOrWhiteSpace(parsedReply.Message)
                ? "Đã ghi nhận. Bạn có thể bổ sung thêm yêu cầu, hoặc bấm \"Write Requirement\" để tạo tài liệu."
                : EndpointQuirks.StripInternalNotices(parsedReply.Message);

            // Lưu suggestions tách riêng (JSON) để UI render chip; chỉ set khi thực sự có gợi ý.
            if (parsedReply.Suggestions.Count > 0)
            {
                suggestionsJson = JsonSerializer.Serialize(parsedReply.Suggestions);
                suggestionsMultiSelect = parsedReply.MultiSelect;
            }

            // Normalize đã đảm bảo OpenEnded ⇒ Suggestions rỗng, nên hai nhánh này loại trừ nhau.
            openEnded = parsedReply.OpenEnded;

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
                    // kept.Count == 0 ⇒ lượt này là câu hỏi TẤT ĐỊNH của BuildFollowUpAfterRepeat (câu
                    // đóng, có sẵn phương án); còn lại thì cờ đi theo câu hỏi thật sự được giữ.
                    openEnded = kept.Count > 0 && trimmed.OpenEnded;
                    flowDiagram = new List<FlowStep>();
                }
            }
            // "Có chip HOẶC là câu mở" = lượt này thật sự đang HỎI. Trước đây vế đầu là đủ vì mọi câu hỏi
            // đều bắt buộc kèm chip; từ khi câu mở được phép bỏ chip, chỉ xét chip là để lọt đúng loại câu
            // đắt nhất (xin lời kể) ra khỏi phanh chống hỏi lại.
            else if ((parsedReply.Suggestions.Count > 0 || parsedReply.OpenEnded)
                     && !RequirementReadinessGate.IsWriteRequirementInvite(reply)
                     && AskedQuestionHistory.IsRepeat(reply, askedKeys))
            {
                // Lượt hỏi MỘT câu, và chính câu đó đã hỏi rồi (Message chở câu hỏi ở đường này).
                var (message, followUpSuggestions) = BuildFollowUpAfterRepeat(project.RequirementCoverageMap);
                reply = message;
                suggestionsJson = followUpSuggestions.Count > 0 ? JsonSerializer.Serialize(followUpSuggestions) : null;
                suggestionsMultiSelect = false;
                openEnded = false;
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
                    // Câu hỏi của gate là câu hỏi đơn thông thường có sẵn phương án — không giữ cờ multi
                    // của lời mời bị thay, và cũng không giữ cờ "câu mở".
                    suggestionsMultiSelect = false;
                    openEnded = false;
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
                    // Lời mời không phải câu hỏi ⇒ không mời người dùng "kể tự do" ở ô nhập.
                    openEnded = false;
                }
            }
            else
            {
                // Sơ đồ luồng chỉ dành cho lượt MỜI bấm nút; lượt hỏi thường mà model lỡ kèm luồng thì bỏ.
                flowDiagram = new List<FlowStep>();
            }

            // BẢNG PHÂN QUYỀN — lượt chốt nhóm «Phân quyền theo nghiệp vụ». Chỉ dựng khi CỔNG đã mở
            // (PermissionMatrixGate: mọi nhóm áp dụng khác đã [RÕ]), nên không lượt nào giữa buổi bị thay
            // bằng một bảng dựng trên phạm vi mới có một nửa.
            //
            // Bảng dựng được ⇒ nó là chỗ trả lời DUY NHẤT của lượt: dọn sạch chip, thẻ hỏi gộp và sơ đồ
            // luồng. Cùng luật với lượt có bảng cột, và cùng lý do — chip bấm là GỬI NGAY, nên một cú bấm
            // nhầm cuốn mất lượt trước khi người dùng kịp chọn xong bảng, và bảng thì không bao giờ được chốt.
            //
            // Model không trả bảng dùng được (structured output tắt, hoặc mọi dòng đều trỏ vào màn hình
            // không có trong phạm vi) ⇒ FAIL-OPEN: lượt chạy y như một lượt chat thường và cổng sẽ mở lại ở
            // lượt sau. Một lượt hỏi thừa rẻ hơn nhiều so với một lượt câm.
            if (askPermissionMatrix)
            {
                permissionMatrix = PermissionMatrixBuilder.Build(parsedReply.PermissionMatrix, plannedScope);
                if (permissionMatrix.Count > 0)
                {
                    // Câu dẫn của model chỉ được dùng khi nó KHÔNG phải lời mời bấm "Write Requirement":
                    // một lời mời đặt trên đầu bảng bảo người dùng bấm nút, trong khi việc thật sự phải làm
                    // nằm ở bảng ngay dưới — đúng kiểu "câu hỏi không có nút trả lời" mà lượt đọc file đã vấp.
                    reply = string.IsNullOrWhiteSpace(parsedReply.Message)
                            || RequirementReadinessGate.IsWriteRequirementInvite(parsedReply.Message)
                        ? PermissionMatrixIntro
                        : EndpointQuirks.StripInternalNotices(parsedReply.Message);
                    suggestionsJson = null;
                    suggestionsMultiSelect = false;
                    openEnded = false;
                    questions = new List<BAChatQuestion>();
                    flowDiagram = new List<FlowStep>();
                }
            }

            // Lượt KỂ LẠI file: chỗ trả lời là hai chip xác nhận, nên không được có thẻ hỏi gộp — thẻ hỏi
            // và chip loại trừ nhau trên màn hình (BAChatReplyParser.Normalize), và một thẻ hỏi ở đây nuốt
            // mất đúng thứ lượt này cần lấy: bản đọc rốt cuộc đúng hay sai. Các câu hỏi khai thác quay lại
            // từ lượt sau.
            if (columnReadbackTurn)
            {
                questions = new List<BAChatQuestion>();
                flowDiagram = new List<FlowStep>();
                // …và bỏ thẻ hỏi thì phải TRẢ LẠI chip: chính vì model kèm thẻ hỏi mà Normalize đã dọn
                // sạch Suggestions của lượt. Để nguyên là bày ra một câu hỏi đóng KHÔNG CÓ nút trả lời —
                // đúng lỗi mà lượt đọc bảng tính đã vấp một lần, chỉ khác chỗ phát sinh.
                if (string.IsNullOrEmpty(suggestionsJson))
                {
                    suggestionsJson = JsonSerializer.Serialize(SourceReadbackSuggestions);
                    suggestionsMultiSelect = false;
                    openEnded = false;
                }
            }
        }

        var flowDiagramJson = flowDiagram.Count > 0 ? JsonSerializer.Serialize(flowDiagram) : null;
        var questionsJson = questions.Count > 0 ? JsonSerializer.Serialize(questions) : null;
        var permissionMatrixJson = permissionMatrix.Count > 0 ? JsonSerializer.Serialize(permissionMatrix) : null;
        await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", reply, suggestionsJson, suggestionsMultiSelect, flowDiagramJson, questionsJson: questionsJson, permissionMatrixJson: permissionMatrixJson, cancellationToken: cancellationToken);

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
            OpenEnded = openEnded,
            Questions = questions,
            // Bản đồ ở thời điểm này đã gộp tới lượt user mới nhất (cập nhật đầu lượt); lượt BA vừa trả
            // lời sẽ được gộp ở lượt sau — đủ tươi cho panel tiến độ.
            Coverage = CoverageMapParser.Parse(project.RequirementCoverageMap).ToList(),
            // Cùng cổng tất định, cùng bản đồ — chỉ khác câu hỏi: "đã đủ vốn chưa" thay vì "lượt này có
            // phải lời mời không". UI cần cả hai vì sau khi bản Brief đã tồn tại, một lượt BA không mời
            // (BA hỏi thêm một câu) không được phép cắt mất đường soạn lại bản Brief đang cũ dần.
            CoverageReady = RequirementReadinessGate.Evaluate(project.RequirementCoverageMap).Ready,
            // "Điều đã chốt" KHÔNG còn chặn đường trả về (một lời gọi LLM ~vài giây mỗi lượt): frame done
            // mang bản đang lưu, bản gộp lượt mới do UpdateDecisionsAsync đẩy ở frame phụ sau done.
            Decisions = DecisionLogService.ParseItems(project.DecisionLog).ToList(),
            FlowDiagram = flowDiagram,
            PermissionMatrix = permissionMatrix,
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

            // PHẠM VI của lượt: file VỪA GỬI, tách khỏi các nguồn cũ. Lượt này nạp lại TOÀN BỘ nguồn của
            // project và điều đó là cố ý — nguồn cũ là thứ duy nhất để ĐỐI CHIẾU, mà chỗ nối giữa file mới
            // và file cũ thường là điểm chưa rõ đắt nhất của cả buổi. Nhưng "đính kèm để đối chiếu" khác
            // hẳn "phải kể lại": không tách hai việc đó ra thì model — vốn thấy mọi nguồn nằm dưới cùng một
            // câu "tôi vừa đính kèm" — kể lại cả những file người dùng đã xác nhận từ lượt trước, và họ phải
            // đọc lại lần thứ hai đúng thứ mình vừa duyệt trước khi tới được phần nói về file vừa gửi.
            var justSentIds = attachments?.Select(a => a.Id).ToHashSet() ?? new HashSet<Guid>();
            var justSentFiles = sources.Where(s => justSentIds.Contains(s.Id)).Select(s => s.FileName).ToList();
            // Không biết lô nào vừa gửi (caller không truyền danh sách) ⇒ giữ nguyên hành vi cũ: coi mọi
            // nguồn là vừa gửi. Đoán bừa một tập con là nguy hiểm hơn hẳn việc kể lại thừa — file vừa gửi
            // mà rơi khỏi phạm vi kể lại thì lượt bắt lỗi đầu vào của chính nó biến mất.
            var scopedToNewFiles = justSentFiles.Count > 0;
            if (!scopedToNewFiles)
                justSentFiles = sources.Select(s => s.FileName).ToList();

            // Ghi chú người dùng gõ cạnh ảnh (nếu có) → BA đọc đúng trọng tâm thay vì tóm tắt chung chung.
            // Câu này GỌI TÊN đúng các file vừa gửi: câu cũ ("đây là các tài liệu nguồn tôi vừa đính kèm")
            // đứng trước text của mọi nguồn nên nó khai man rằng file cũ cũng vừa được gửi.
            var justSentList = string.Join(", ", justSentFiles);
            var promptText = string.IsNullOrEmpty(trimmedNote)
                ? $"Tôi vừa đính kèm: {justSentList}. Bạn đọc kỹ và kể lại cụ thể những gì rút được từ đó để tôi xác nhận nhé."
                : $"Tôi vừa đính kèm: {justSentList}, kèm ghi chú của tôi: \"{trimmedNote}\". Bạn đọc kỹ và kể lại cụ thể những gì rút được từ đó để tôi xác nhận nhé.";

            var userContent = new List<AIContent> { new TextContent(promptText) };
            userContent.AddRange(sourceContents.Contents);

            // HÌNH DẠNG của lượt do CƠ CHẾ chọn, không để model tự đoán (cùng luật với cổng bảng phân
            // quyền): còn bảng tính chưa chốt cột ⇒ lượt CHỐT PHẠM VI CỘT (bảng + lời giới thiệu ngắn);
            // không còn ⇒ lượt BẢN ĐỌC LẠI như cũ. Model nhìn thấy text của MỌI nguồn trong project (kể cả
            // file đã chốt cột từ lần upload trước) nên nó không tự suy ra được file nào đang chờ.
            var pendingColumnFiles = sources
                .Where(s => s.Kind == SourceFileKind.Spreadsheet && s.ExtractedText != null && s.ColumnMap == null)
                .Select(s => s.FileName)
                .ToList();

            // Nguồn CŨ mà lượt này không còn việc gì với nó. Bảng tính cũ chưa chốt cột KHÔNG nằm ở đây dù
            // nó cũng là nguồn cũ: bảng của nó được bày lại ngay lượt này (BuildColumnMapJson lấy mọi file
            // ColumnMap == null), nên nó vẫn cần một câu giới thiệu — cấm nhắc tới nó là mời rà một cái bảng
            // không có lời dẫn nào.
            var earlierFiles = scopedToNewFiles
                ? sources
                    .Where(s => !justSentIds.Contains(s.Id) && !pendingColumnFiles.Contains(s.FileName))
                    .Select(s => s.FileName)
                    .ToList()
                : new List<string>();

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/source-ack.v3.md")),
                new(ChatRole.System, BuildSourceAckTurnShape(pendingColumnFiles, justSentFiles, earlierFiles)),
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
                callError = ex.Message;
            }

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
                    $"{ConversationTranscriptBuilder.LlmFailurePrefix}, chưa thể đọc và tóm tắt tài liệu vừa gửi. Chi tiết: {detail}",
                    cancellationToken: cancellationToken);
                return false;
            }

            // BẢNG CỘT cho các nguồn dạng bảng tính (xem SourceColumnMapBuilder). Structured output tắt ⇒
            // parsed null ⇒ thử đọc lại từ raw content, đúng như phần SourceNotes bên dưới.
            var proposedColumns = parsed?.Columns
                ?? LlmJson.TryDeserialize<BASourceAckReply>(callResult?.Content, requireKnownProperty: true)?.Columns;
            var columnMapJson = BuildColumnMapJson(sources, proposedColumns);

            // Lượt lẽ ra phải có bảng mà rốt cuộc không dựng được (model không trả `columns` dùng được):
            // message đã được viết theo hình dạng "mời rà bảng bên dưới" nên nó đang trỏ vào một cái bảng
            // KHÔNG tồn tại. Nói thẳng ra và mở đường khác, thay vì để người dùng đi tìm một cái bảng không
            // có — phạm vi cột lúc này quay về đường phỏng vấn như trước khi có bảng.
            if (pendingColumnFiles.Count > 0 && columnMapJson == null)
                reply.Message = reply.Message.TrimEnd() + ColumnMapMissingNotice;

            // Có bảng cột ⇒ BỎ hàng chip của lượt này. Chip của lượt đọc file là câu chốt bản đọc lại
            // ("Đúng rồi" / "Chưa đúng"), mà bấm chip là GỬI NGAY — để cả hai cùng sống thì một cú bấm
            // nhầm gửi mất lượt trước khi người dùng kịp tích xong bảng, và bảng thì không bao giờ được
            // chốt. Cùng luật với "lượt gộp có Questions ⇒ bỏ Suggestions" trong BAChatReplyParser.Normalize.
            var suggestionsJson = columnMapJson == null && reply.Suggestions.Count > 0
                ? JsonSerializer.Serialize(reply.Suggestions)
                : null;
            await _conversationLog.AppendAsync(projectId, ba.Id, "assistant", reply.Message.Trim(), suggestionsJson,
                columnMapJson == null && reply.MultiSelect, columnMapJson: columnMapJson, cancellationToken: cancellationToken);

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
    /// Khối chọn HÌNH DẠNG của lượt đọc file, dựng từ dữ liệu chứ không để model tự đoán.
    ///
    /// <para>
    /// Còn bảng tính chưa chốt cột ⇒ lượt này chỉ bày BẢNG CỘT kèm một lời giới thiệu ngắn. Bản đọc lại chi
    /// tiết của bảng tính (và cụm "Chỗ chưa chắc" của nó) bị đẩy sang lượt sau — lượt mà người dùng đã chốt
    /// xong phạm vi cột — vì kể lại cả file trước khi biết họ dùng cột nào là: dựng việc tồn trên những cột
    /// sắp bị bỏ tích, đọc nhầm cả file khi người dùng gửi nhầm file, và bày ra một bức tường chữ ngay trên
    /// đúng cái bảng chở cùng nội dung ở dạng sửa được. Xem <c>source-readback.v1.md</c> cho nửa còn lại.
    /// </para>
    ///
    /// <para>
    /// Khối này mang thêm PHẠM VI KỂ LẠI — các file thật sự vừa gửi ở lượt này. Lượt đọc file nạp lại toàn
    /// bộ nguồn của project, còn model thì không có cách nào tự phân biệt file mới với file đã xác nhận từ
    /// lượt trước, nên thiếu dòng này nó kể lại tất — xem <see cref="BuildReadbackScope"/>.
    /// </para>
    /// </summary>
    private static string BuildSourceAckTurnShape(
        IReadOnlyList<string> pendingColumnFiles,
        IReadOnlyList<string> justSentFiles,
        IReadOnlyList<string> earlierFiles)
    {
        var shape = pendingColumnFiles.Count > 0
            ? "## LƯỢT NÀY: CHỐT PHẠM VI CỘT (bắt buộc)\n"
              + "File bảng tính CHƯA chốt bảng cột: " + string.Join(", ", pendingColumnFiles) + ".\n"
              + "Với các file đó, lượt này CHỈ làm hai việc: điền `columns` phủ đủ mọi cột của file (ý nghĩa "
              + "viết sẵn, tích sẵn cột nghiệp vụ), và viết `message` NGẮN — tối đa năm câu: file này là gì, "
              + "quy mô thật, rồi mời người dùng rà bảng bên dưới và bấm \"Gửi bảng cột\".\n"
              + "TUYỆT ĐỐI KHÔNG kể lại chi tiết từng cột và KHÔNG viết cụm \"Chỗ chưa chắc\" cho các file "
              + "đó: bản đọc lại của bảng tính là lượt SAU, sau khi người dùng chốt xong cột. Ngoại lệ duy "
              + "nhất là MỘT câu khi file rõ ràng không phải thứ bạn vừa xin — họ cần biết ngay để gửi lại.\n"
              + "Nguồn khác trong cùng lô (Word/PDF/ảnh) vẫn được đọc lại đầy đủ như thường."
            : "## LƯỢT NÀY: BẢN ĐỌC LẠI\n"
              + "Không có file bảng tính nào đang chờ chốt bảng cột ⇒ `columns` là mảng RỖNG, và lượt này là "
              + "bản đọc lại đầy đủ: kể lại thứ bạn đọc được, nêu cụm \"Chỗ chưa chắc\", kết bằng câu hỏi "
              + "đóng để người dùng bấm một trong hai chip.";

        return shape + BuildReadbackScope(justSentFiles, earlierFiles);
    }

    /// <summary>
    /// PHẠM VI KỂ LẠI của lượt đọc file: gọi đích danh các file VỪA GỬI, và nói thẳng rằng các nguồn cũ chỉ
    /// đính kèm để đối chiếu.
    ///
    /// <para>
    /// Ca thật đã gặp: người dùng đã chốt bảng cột cho một file Excel từ đầu buổi, nhiều lượt sau gửi thêm
    /// một ảnh chụp biểu mẫu để trả lời một câu hỏi — và BA kể lại CẢ HAI, mở đầu lượt bằng gần nửa số dòng
    /// nói lại đúng bộ cột người dùng đã tích tay ở lượt trước. Model không sai luật nào nó được cho: nó
    /// thấy text của mọi nguồn nằm dưới cùng một câu "tôi vừa đính kèm", và prompt bắt "MỌI file vừa gửi
    /// đều phải được nhắc tới". Chỗ hỏng là cơ chế nói dối về chữ "vừa gửi", nên chỗ vá cũng phải ở đây —
    /// prompt không có cách nào tự biết file nào mới.
    /// </para>
    ///
    /// <para>
    /// Nguồn cũ vẫn ĐI KÈM, và ngoại lệ cho phép nhắc tên chúng là cố ý: điểm chưa rõ đắt nhất của một lô
    /// upload thường nằm đúng ở chỗ NỐI giữa file mới và file cũ (biểu mẫu vừa gửi lấy người học từ file
    /// danh sách hay tự nhập?). Cấm nhắc tên là cắt luôn câu hỏi đó.
    /// </para>
    /// </summary>
    private static string BuildReadbackScope(IReadOnlyList<string> justSentFiles, IReadOnlyList<string> earlierFiles)
    {
        if (justSentFiles.Count == 0)
            return string.Empty;

        var scope = "\n\n## PHẠM VI KỂ LẠI CỦA LƯỢT NÀY\n"
            + "File người dùng VỪA GỬI ở lượt này: " + string.Join(", ", justSentFiles) + ". Chỉ những file "
            + "này mới là thứ lượt này phải kể lại và xin xác nhận.\n";

        if (earlierFiles.Count == 0)
            return scope;

        return scope
            + "Các nguồn còn lại — " + string.Join(", ", earlierFiles) + " — đã gửi từ TRƯỚC và người dùng đã "
            + "xác nhận cách bạn hiểu chúng rồi; chúng đính kèm ở đây CHỈ để bạn đối chiếu. TUYỆT ĐỐI KHÔNG "
            + "kể lại chúng: không mô tả lại nội dung/cột/quy mô của chúng, không dựng cụm \"Chỗ chưa chắc\" "
            + "cho riêng chúng. Kể lại một file đã được xác nhận là bắt người dùng đọc và duyệt lần thứ hai "
            + "đúng thứ họ vừa duyệt, trong khi file họ thật sự vừa gửi bị đẩy xuống nửa dưới của lượt.\n"
            + "Được phép nhắc tên chúng đúng MỘT trường hợp: nêu một điểm chưa rõ nằm ở chỗ NỐI giữa file "
            + "vừa gửi và chúng (dữ liệu bên này lấy từ bên kia hay nhập tay?). Đó là câu hỏi chỉ lộ ra khi "
            + "đặt hai nguồn cạnh nhau, và nó thuộc về file vừa gửi.";
    }

    /// <summary>
    /// Dựng bảng cột cho các nguồn BẢNG TÍNH của project từ đề xuất của model. Trả null khi không có gì để
    /// hỏi — khi đó lượt đọc file chạy đúng như trước (chỉ bản đọc lại + chip xác nhận).
    ///
    /// <para>
    /// Bỏ qua file ĐÃ chốt bảng cột (<see cref="ProjectSourceFile.ColumnMap"/> khác null): lượt đọc file
    /// nạp lại TOÀN BỘ nguồn của project, nên không có bộ lọc này thì mỗi lần người dùng đính thêm một file
    /// mới là các bảng đã chốt trước đó hiện lại y nguyên để tích lần nữa.
    /// </para>
    /// </summary>
    private static string? BuildColumnMapJson(List<ProjectSourceFile> sources, List<SourceColumnNote>? proposed)
    {
        if (proposed is not { Count: > 0 })
            return null;

        var pending = sources
            .Where(s => s.Kind == SourceFileKind.Spreadsheet && s.ExtractedText != null && s.ColumnMap == null)
            .ToList();
        if (pending.Count == 0)
            return null;

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

        var rows = new List<SourceColumnNote>();
        foreach (var source in pending)
            rows.AddRange(SourceColumnMapBuilder.Build(source.FileName, proposed, SourceColumnMapBuilder.ReadColumnNames(source.ExtractedText)));

        return rows.Count > 0 ? JsonSerializer.Serialize(rows) : null;
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
            .Select(q => new { group = q.Group, question = q.Question, suggestions = q.Suggestions, multiSelect = q.MultiSelect, openEnded = q.OpenEnded });
        return JsonSerializer.Serialize(new { message = c.Message, suggestions, multiSelect = c.SuggestionsMultiSelect, questions, ready, flowDiagram });
    }
}
