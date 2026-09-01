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
    private readonly InterviewOutlookService _interviewOutlook;
    private readonly ScreenStepPlacementService _stepPlacement;
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
    /// Hai chip dự phòng của NHỊP TÓM TẮT KIỂM CHỨNG — bộ mà <c>requirement-chat.v4.md</c> kê sẵn cho lượt
    /// này. Lượt tóm tắt là câu ĐÓNG (gật, hoặc đòi sửa), nên nó phải có nút để bấm; thiếu nút thì model
    /// tự trượt sang hỏi độ ĐẦY ĐỦ của cả buổi phỏng vấn và nhận về một lời tuyên bố hoàn tất mà bản đồ
    /// bao phủ không hề công nhận (xem chốt chặn ở <see cref="ChatAsync"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> SummaryCheckSuggestions =
        new[] { "Đúng rồi, tiếp tục", "Tôi muốn sửa lại" };

    /// <summary>
    /// Lượt này có phải một nhịp tóm tắt kiểm chứng không: BA đang phát lại cách mình hiểu rồi xin xác
    /// nhận. Nhận diện bằng CỤM TỪ + dấu hỏi, cố ý hẹp như <c>NarrativeCues</c> — bắt hụt thì lượt đó chỉ
    /// mất tiện ích bấm chip, còn bắt quá tay thì gắn chip xác nhận vào một câu hỏi khai thác thật.
    /// </summary>
    private static bool LooksVerificationSummary(string? message)
    {
        var value = (message ?? string.Empty).ToLowerInvariant();
        if (!value.Contains('?', StringComparison.Ordinal))
            return false;

        return SummaryCues.Any(cue => value.Contains(cue, StringComparison.Ordinal));
    }

    private static readonly string[] SummaryCues =
    {
        "tóm tắt lại", "mình tóm tắt", "xin tóm tắt", "tổng hợp lại", "mình hiểu đúng", "mình đang hiểu"
    };

    /// <summary>
    /// Câu dẫn dự phòng cho lượt bày bảng phân quyền, dùng khi model không viết được câu dẫn dùng được.
    /// Nó phải CHỈ VÀO BẢNG chứ không kết bằng một câu hỏi đóng: lượt này không có chip, nên một câu hỏi
    /// ở đây là câu hỏi KHÔNG CÓ NÚT TRẢ LỜI — người dùng đi tìm nút "Đúng rồi" không thấy trong khi việc
    /// thật sự phải làm nằm ngay dưới. Đúng lỗi mà lượt đọc bảng tính đã vấp một lần.
    /// </summary>
    public const string PermissionMatrixIntro =
        "Các phần khác mình đã ghi nhận đủ, còn lại phần phân quyền. Mình đã dựng sẵn bảng bên dưới "
        + "theo các màn hình đã chốt: anh/chị chọn phạm vi cho từng vai trò (ô để trống là vai đó không có quyền), "
        + "rồi bấm \"Gửi bảng phân quyền\" giúp mình nhé.";

    /// <summary>
    /// Câu dẫn dự phòng cho ba lượt bảng còn lại, cùng luật với <see cref="PermissionMatrixIntro"/>: CHỈ VÀO
    /// BẢNG, không kết bằng câu hỏi đóng. Lượt có bảng không có chip, nên một câu hỏi ở đây là câu hỏi KHÔNG
    /// CÓ NÚT TRẢ LỜI — người dùng đi tìm nút "Đúng rồi" không thấy trong khi việc thật sự phải làm nằm ngay
    /// dưới.
    /// </summary>
    public const string FlowMapIntro =
        "Mình ráp lại các luồng nghiệp vụ từ những gì anh/chị đã kể. Anh/chị xem giúp bảng bên dưới: bước nào "
        + "sai thì sửa hoặc bỏ tích, rồi bấm \"Gửi bảng luồng\" nhé.";

    /// <inheritdoc cref="FlowMapIntro"/>
    public const string ScreenScopeIntro =
        "Từ các luồng vừa chốt, mình liệt kê các màn hình ứng dụng sẽ có. Anh/chị bỏ tích màn hình không cần và "
        + "sửa lại phần việc của từng màn cho đúng, rồi bấm \"Gửi bảng màn hình\" giúp mình nhé.";

    /// <summary>Số mục mới được GỌI TÊN trong câu dẫn bày lại; phần dư gộp thành "và N mục khác".</summary>
    private const int MaxNamedNewScreens = 4;

    /// <summary>
    /// Câu dẫn cho lượt bày LẠI bảng màn hình — cổng duy nhất mở lại được sau khi đã chốt (xem
    /// <see cref="ScreenScopeGate"/>).
    ///
    /// <para>
    /// Vì sao nó phải là một câu dẫn RIÊNG chứ không dùng lại <see cref="ScreenScopeIntro"/>: với người
    /// dùng, một bảng màn hình hiện ra lần thứ hai kèm đúng lời mời cũ đọc lên là *"BA quên mình vừa gửi
    /// bảng này rồi"* — và đó là hiểu lầm đắt, vì luật "không hỏi lại điều đã trả lời" là thứ họ dùng để
    /// đánh giá cả buổi phỏng vấn. Cơ chế thì đã làm đúng: <see cref="ScreenScopeMapBuilder.SeedRows"/> giữ
    /// nguyên phần họ đã tự tay rà và việc thật sự còn lại chỉ là mấy màn hình mới — nhưng KHÔNG có gì trên
    /// màn hình nói điều đó. Câu dẫn này là chỗ duy nhất nói được.
    /// </para>
    ///
    /// <para>
    /// Phần trôi không chỉ là màn hình mới: một CHỨC NĂNG lộ ra trên một màn hình đã chốt cũng phải được
    /// gọi tên ở đây, và phải gọi khác đi — người dùng nghe "còn 2 màn hình nữa" rồi mở ra thấy đúng bảng
    /// cũ với một dòng con lạ sẽ đi tìm cái màn hình không có thật.
    /// </para>
    /// </summary>
    public static string ScreenScopeReshowIntro(IReadOnlyList<string> newScreens, IReadOnlyList<string> newFunctions)
    {
        var items = newScreens.Concat(newFunctions).ToList();
        var named = items.Take(MaxNamedNewScreens).Select(s => $"“{s}”").ToList();
        var rest = items.Count - named.Count;
        var list = string.Join(", ", named) + (rest > 0 ? $" và {rest} mục khác" : string.Empty);

        // Gọi đúng tên loại: toàn màn hình mới thì nói "màn hình", có lẫn chức năng thì nói "mục" — một câu
        // dẫn sai loại đắt đúng bằng một câu dẫn không có.
        var noun = newFunctions.Count == 0 ? "màn hình" : "mục";
        var count = items.Count == 1 ? $"một {noun}" : $"{items.Count} {noun}";

        return "Phần bảng màn hình anh/chị đã chốt mình giữ nguyên, không phải rà lại. Từ những điều anh/chị "
            + $"nói sau đó, mình thấy còn {count} nữa cần chốt: {list}. Anh/chị xem giúp phần này — không cần "
            + "thì bỏ tích — rồi bấm \"Gửi bảng màn hình\" nhé.";
    }

    /// <inheritdoc cref="FlowMapIntro"/>
    public const string EntityMapIntro =
        "Mình tổng hợp các đối tượng nghiệp vụ mà ứng dụng cần lưu, kèm các trạng thái chúng đi qua. Anh/chị rà "
        + "giúp bảng bên dưới rồi bấm \"Gửi bảng đối tượng\" nhé.";

    /// <inheritdoc cref="FlowMapIntro"/>
    public const string ReportMapIntro =
        "Từ những gì anh/chị đã kể, mình gom lại các báo cáo mà ứng dụng cần có. Anh/chị rà giúp bảng bên dưới — "
        + "cái nào không cần thì bỏ tích, thiếu thì bấm \"+ thêm báo cáo\" — rồi bấm \"Gửi bảng báo cáo\" nhé.";

    /// <inheritdoc cref="FlowMapIntro"/>
    public const string NotificationMapIntro =
        "Còn đúng một việc cuối: ai cần nhận email khi có việc gì xảy ra. Mình liệt kê sẵn các sự kiện bên dưới — "
        + "anh/chị bỏ tích sự kiện không cần báo, chọn người nhận cho các sự kiện còn lại, rồi bấm "
        + "\"Gửi bảng thông báo\" giúp mình nhé.";

    /// <summary>
    /// Câu nói thêm khi BA vừa TỰ XẾP CHỖ cho các bước luồng chưa ai phụ trách
    /// (<see cref="ScreenStepPlacementService"/>). Trả null khi không xếp được gì.
    ///
    /// <para>
    /// Vì sao việc xếp chỗ không được im lặng, dù mọi dòng đều tích sẵn và người dùng vẫn rà được: một
    /// MÀN HÌNH mới xuất hiện giữa bảng mà không câu nào nói vì sao thì người dùng chỉ có hai cách hiểu —
    /// hoặc họ đọc sót ở lượt trước, hoặc BA tự tiện thêm. Cùng luật với
    /// <c>ScreenScopeMapBuilder.RenderUserMessage</c> kể lại các dòng người dùng tự thêm: thứ vào phạm vi
    /// bằng một đường khác thường phải được gọi tên ở đúng lượt nó vào.
    /// </para>
    ///
    /// <para>
    /// Câu này dựng từ BẢNG SAU KHI XẾP chứ không từ lời model trả về: chỗ ở thật của một bước là chỗ
    /// <c>ApplyPlacements</c> đã ghi nó vào, và các mục bị chốt chặn ở đó bỏ đi thì không được kể như đã
    /// làm. Vế "trước" chỉ là các TÊN màn hình, không phải bảng cũ: <c>ApplyPlacements</c> trả một danh
    /// sách mới nhưng dùng lại chính các object dòng, nên một tham chiếu tới bảng cũ không phải một ảnh
    /// chụp — nó đổi theo.
    /// </para>
    /// </summary>
    public static string? ScreenScopePlacementNotice(
        IReadOnlyList<string> screensBefore,
        IReadOnlyList<ScreenScopeRow> after,
        IReadOnlyList<string> placedSteps)
    {
        if (placedSteps.Count == 0)
            return null;

        var lines = new List<string>();
        foreach (var step in placedSteps)
        {
            var home = after
                .Where(r => r.Included)
                .SelectMany(r => r.Functions.Where(f => f.Included).Select(f => new { Row = r, Function = f }))
                .FirstOrDefault(x => x.Function.FlowSteps.Any(s =>
                    string.Equals(s.Trim(), step.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (home != null)
                lines.Add($"• “{step}” → {home.Row.Screen} · “{home.Function.Name}”");
        }

        if (lines.Count == 0)
            return null;

        var known = new HashSet<string>(screensBefore, StringComparer.OrdinalIgnoreCase);
        var addedScreens = after.Select(r => r.Screen).Where(s => !known.Contains(s)).ToList();

        var notice = "\n\nCó mấy bước trong luồng anh/chị đã chốt mà chưa chức năng nào phụ trách, mình xếp "
            + "vào chỗ hợp lý nhất rồi — anh/chị xem giúp có đúng không:\n"
            + string.Join("\n", lines);

        if (addedScreens.Count > 0)
        {
            notice += $"\nTrong đó {string.Join(", ", addedScreens.Select(s => $"“{s}”"))} là màn hình mình "
                + "thêm mới vì không màn nào đang có làm được việc đó — không cần thì anh/chị bỏ tích giúp mình.";
        }

        return notice;
    }


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
        InterviewOutlookService interviewOutlook,
        ScreenStepPlacementService stepPlacement,
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
        _interviewOutlook = interviewOutlook;
        _stepPlacement = stepPlacement;
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
        project.InterviewOutlookHarvestedTurnCount = Math.Min(project.InterviewOutlookHarvestedTurnCount, beforeEdited);
        project.UserMemoryHarvestedTurnCount = Math.Min(project.UserMemoryHarvestedTurnCount, beforeEdited);
        project.SummarizedTurnCount = Math.Min(project.SummarizedTurnCount, beforeEdited);
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
    /// cổng nào đang mở, phạm vi màn hình thật, các dòng gieo của bảng thông báo. Tính lại ở nhánh thứ hai
    /// là mở đường cho hai nhánh xét trên hai bản khác nhau, đúng loại vênh mà cả tầng cổng tất định của
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
        bool ColumnReadbackTurn,
        List<string> AskedBefore,
        InterviewTableKind Table,
        bool ReshowScreenScope,
        List<string> PendingScreens,
        List<string> PendingFunctions,
        IReadOnlyList<string> EffectiveScreens,
        List<string> EntityNames,
        List<NotificationMapRow> NotificationSeedRows,
        List<string> RecipientOptions)
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
        onStatus?.Invoke("BA đang đọc lại ngữ cảnh hội thoại…");
        var turn = await BuildTurnContextAsync(project, ba, model, cancellationToken);

        var messages = await BuildMessagesAsync(turn, ba, cancellationToken);

        onStatus?.Invoke("BA đang soạn câu trả lời…");

        // BA được nhắc trả JSON {message, suggestions}: dùng structured output khi model được bật, ngược lại
        // parser luôn fallback an toàn về text thuần. Khi có onToken, luồng token thô (cú pháp JSON) được
        // lọc qua BAChatTokenFilter để chỉ phần message hiển thị được stream lên UI; đường structured
        // output vốn không stream nên callback đơn giản là không được gọi — UI vẫn nhận bản chốt ở done.
        var tokenFilter = onToken == null ? null : new BAChatTokenFilter(onToken);
        var (callResult, structuredReply) = await _llm.ChatStructuredAsync<BAChatReply>(
            model, messages, ba.Temperature, new ModelCallLogContext(turn.ProjectId, ba, "BAChat"),
            tokenFilter == null ? null : tokenFilter.Feed, cancellationToken);

        var draft = new BAChatTurnDraft();
        if (!callResult.IsSuccess)
        {
            // Lỗi gọi model thành một lượt assistant CÓ NHÃN thay vì một HTTP 500 — nhưng không bao giờ
            // bày một lỗi API ra như thể đó là câu trả lời bình thường của BA. Tiền tố dùng chung với
            // ConversationTranscriptBuilder để transcript tổng hợp yêu cầu lọc được các lượt lỗi này ra.
            draft.Reply = $"{ConversationTranscriptBuilder.LlmFailurePrefix}, chưa thể trả lời. Chi tiết: {callResult.ErrorMessage ?? callResult.Content}";
        }
        else
        {
            await ShapeTurnAsync(draft, callResult, structuredReply, turn, ba, model, cancellationToken);
        }

        return await SaveTurnAsync(turn, ba, draft, cancellationToken);
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
        var (memory, userMemory, coverageUpdate) = await PrepareTurnContextAsync(project, ba, model, cancellationToken);
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
        var sourceContents = _sourceContextBuilder.Build(sources, model);
        var lastUserIndex = recent.FindLastIndex(c => c.Role != "assistant");

        // LƯỢT KỂ LẠI FILE BẢNG TÍNH: người dùng vừa gửi bảng cột đã tích, và tin nhắn đó do server soạn
        // nên nhận ra được chắc chắn (SourceColumnMapBuilder.IsSubmissionMessage). Bản đọc lại của bảng
        // tính bị cố ý dời từ lượt upload xuống đây — xem BASourceAckPrompt.TurnShape. Không có lượt này thì
        // chốt cột xong là BA hỏi thẳng câu tiếp theo, và cái sai duy nhất còn lại ở đầu vào (BA hiểu file
        // kể chuyện gì) không bao giờ được người dùng nhìn thấy để bác.
        var columnReadbackTurn = lastUserIndex >= 0
            && SourceColumnMapBuilder.IsSubmissionMessage(recent[lastUserIndex].Message)
            && sources.Any(s => s.Kind == SourceFileKind.Spreadsheet && !string.IsNullOrWhiteSpace(s.ColumnMap));

        // MỘT cổng cho cả sáu bảng, chọn TẤT ĐỊNH và mỗi lượt đúng MỘT bảng: hai khối "## LƯỢT NÀY:" cùng
        // lúc là hai mệnh lệnh chọi nhau, model sẽ trả một bảng lai hoặc bỏ cả hai. Lượt kể lại file đã có
        // việc riêng và chỉ có MỘT chỗ trả lời (hai chip xác nhận) nên mọi cổng nhường nó một lượt — chúng
        // mở lại ngay lượt sau. Xem InterviewTableGate cho thứ tự ưu tiên và lý do.
        var table = InterviewTableGate.Select(project, suppressed: columnReadbackTurn);

        // Bảng màn hình là cổng DUY NHẤT mở lại được sau khi đã chốt (xem ScreenScopeGate). Bảng đã mang
        // dấu chốt ⇒ lượt BÀY LẠI, và cả khối lệnh lẫn câu dẫn đều phải nói ra sự khác biệt đó: phần lớn
        // bảng là thứ người dùng đã tự tay rà và hệ thống giữ nguyên bằng SeedRows, việc của lượt này chỉ
        // là các mục vừa lộ ra. Ở lượt bày ĐẦU thì MỌI dòng đều chờ duyệt, nên hai danh sách dưới đây cố ý
        // để rỗng: gọi tên cả bảng là "phần mới" thì câu dẫn thành vô nghĩa.
        var reshowScreenScope = table == InterviewTableKind.ScreenScope
            && ScreenScopeMapBuilder.IsConfirmed(project.ScreenScopeMap);

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
            columnReadbackTurn,
            // Sổ "đã hỏi rồi": bản đồ bao phủ chỉ có độ phân giải theo NHÓM, nên một nhóm chưa [RÕ] dễ
            // khiến model phát lại nguyên văn câu hỏi mở đầu của chính nhóm ấy. Danh sách câu hỏi thật là
            // thứ duy nhất phân biệt được "hỏi tiếp phần còn thiếu" với "hỏi lại điều vừa được trả lời".
            AskedQuestionHistory.Collect(recent),
            table,
            reshowScreenScope,
            reshowScreenScope ? ScreenScopeMapBuilder.PendingScreens(project.ScreenScopeMap) : new List<string>(),
            reshowScreenScope ? ScreenScopeMapBuilder.PendingFunctions(project.ScreenScopeMap) : new List<string>(),
            // PHẠM VI MÀN HÌNH THẬT SỰ: mọi dòng CÒN TÍCH của bảng màn hình — nguồn duy nhất, chốt rồi hay
            // còn chờ duyệt. Đây là chỗ bảng màn hình trả tiền cho chính nó, vì các DÒNG của bảng phân
            // quyền lấy từ đây.
            PermissionMatrixGate.EffectiveScreens(project),
            // Bộ đối chiếu ô "lấy số từ" của bảng BÁO CÁO: tên các đối tượng người dùng đã GIỮ ở bảng đối
            // tượng. Cùng lý do với hai đầu vào của bảng thông báo ngay dưới — cả khối "LƯỢT NÀY" lẫn nhánh
            // dựng bảng đều đọc nó, nên tính một lần ở đây.
            EntityMapBuilder.EntityNames(project.EntityMap),
            // Hai đầu vào của bảng THÔNG BÁO: DÒNG là chuyển trạng thái của bảng đối tượng đã chốt, còn MỤC
            // CHỌN của hai ô To/CC là danh sách người nhận của dự án — bộ người dùng đã tự tay rà nếu có,
            // còn không thì bản gieo từ bốn quan hệ + các vai trò của bảng phân quyền.
            NotificationMapBuilder.SeedRows(project.EntityMap),
            NotificationMapBuilder.RecipientOptions(
                project.NotificationRecipients, PermissionMatrixBuilder.Roles(project.PermissionMatrix)));
    }

    /// <summary>
    /// Lắp ngữ cảnh của lượt thành danh sách message gửi model. Nội dung từng khối nằm ở
    /// <see cref="BAChatPromptBlocks"/>; ở đây chỉ có THỨ TỰ và điều kiện đính — và thứ tự là một phần của
    /// hành vi, xem ghi chú prompt cache ngay dưới.
    /// </summary>
    private async Task<List<ChatMessage>> BuildMessagesAsync(TurnContext turn, Agent ba, CancellationToken cancellationToken)
    {
        var project = turn.Project;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/requirement-chat.v4.md"))
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

        if (!string.IsNullOrWhiteSpace(turn.Memory.Summary))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.ConversationMemory(turn.Memory.Summary)));

        if (!string.IsNullOrWhiteSpace(turn.CoverageUpdate.Map))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.CoverageMap(turn.CoverageUpdate.Map)));

        var openQuestions = InterviewOutlookService.ParseItems(project.OpenQuestions);
        if (openQuestions.Count > 0)
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.OpenQuestions(openQuestions)));

        AppendTableBlocks(messages, turn);

        // LƯỢT KỂ LẠI FILE BẢNG TÍNH — nửa sau của cơ chế "bảng cột trước, bản đọc lại sau". Luật viết bản
        // đọc lại nằm trong prompt riêng (đo được ở Prompt Evals, sửa được ở Prompt Studio) chứ không nhét
        // thành chuỗi ở đây; khối này chỉ chọn ĐÚNG lượt để đính nó vào.
        if (turn.ColumnReadbackTurn)
            messages.Add(new ChatMessage(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/source-readback.v1.md")));

        var askedNote = AskedQuestionHistory.BuildNote(turn.AskedBefore);
        if (!string.IsNullOrWhiteSpace(askedNote))
            messages.Add(new ChatMessage(ChatRole.System, askedNote));

        AppendTranscript(messages, turn);
        return messages;
    }

    /// <summary>
    /// Sáu khối bảng, chia làm hai loại: bảng ĐÃ CHỐT (đính vào mọi lượt sau, không phụ thuộc cổng nào
    /// đang mở) và đúng MỘT khối "## LƯỢT NÀY:" của cổng đang mở. Chúng loại trừ nhau vì cùng đến từ một
    /// lời gọi <see cref="InterviewTableGate.Select"/>.
    /// </summary>
    private static void AppendTableBlocks(List<ChatMessage> messages, TurnContext turn)
    {
        var project = turn.Project;

        AppendConfirmedTable(messages, FlowMapBuilder.RenderConfirmedBlock(project.FlowMap), BAChatPromptBlocks.ConfirmedFlowMap);
        AppendConfirmedTable(messages, ScreenScopeMapBuilder.RenderConfirmedBlock(project.ScreenScopeMap), BAChatPromptBlocks.ConfirmedScreenScope);
        AppendConfirmedTable(messages, EntityMapBuilder.RenderConfirmedBlock(project.EntityMap), BAChatPromptBlocks.ConfirmedEntityMap);
        AppendConfirmedTable(messages, ReportMapBuilder.RenderConfirmedBlock(project.ReportMap), BAChatPromptBlocks.ConfirmedReportMap);

        // PHÂN QUYỀN — ba trạng thái, ba lệnh khác nhau, và lệnh nào cũng do CƠ CHẾ chọn chứ không để model
        // tự đoán đang ở trạng thái nào: đã chốt / lượt bày bảng / còn phải chờ (cấm hỏi lẻ).
        var confirmedMatrix = PermissionMatrixBuilder.RenderConfirmedBlock(project.PermissionMatrix);
        if (!string.IsNullOrWhiteSpace(confirmedMatrix))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.ConfirmedPermissionMatrix(confirmedMatrix)));
        else if (turn.Table == InterviewTableKind.PermissionMatrix)
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.PermissionMatrixTable(turn.EffectiveScreens)));
        else
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.PermissionMatrixDeferred));

        switch (turn.Table)
        {
            case InterviewTableKind.FlowMap:
                messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.FlowMapTable));
                break;

            case InterviewTableKind.ScreenScope:
                messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.ScreenScopeTable(
                    turn.ReshowScreenScope, turn.EffectiveScreens, turn.PendingScreens, turn.PendingFunctions, project.FlowMap)));
                break;

            case InterviewTableKind.EntityMap:
                messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.EntityMapTable));
                break;

            case InterviewTableKind.ReportMap:
                messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.ReportMapTable(turn.EntityNames)));
                break;
        }

        // THÔNG BÁO / NHẮC NHỞ — bốn ca: đã chốt / lượt bày bảng / còn phải chờ (cấm hỏi lẻ) / dự án không
        // có vòng đời nào. Ca cuối KHÔNG có lệnh nào: bảng sẽ không bao giờ được bày, nên giữ lệnh cấm là
        // khóa chết nhóm ở [CHƯA HỎI] và nút "Write Requirement" không bao giờ sáng. Đây là đường thoát duy
        // nhất của ca đó, và nó khớp đúng điều kiện thứ ba của NotificationMapGate.
        var confirmedNotifications = NotificationMapBuilder.RenderConfirmedBlock(project.NotificationMap);
        if (!string.IsNullOrWhiteSpace(confirmedNotifications))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.ConfirmedNotificationMap(confirmedNotifications)));
        else if (turn.Table == InterviewTableKind.NotificationMap)
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.NotificationMapTable(turn.NotificationSeedRows, turn.RecipientOptions)));
        else if (turn.NotificationSeedRows.Count > 0 || !PermissionMatrixGate.IsConfirmed(project.PermissionMatrix))
            messages.Add(new ChatMessage(ChatRole.System, BAChatPromptBlocks.NotificationDeferred));
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
        BAChatTurnDraft draft, LlmCallResult callResult, BAChatReply? structuredReply,
        TurnContext turn, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        // Đường structured output trả thẳng BAChatReply (không qua Parse), nên phải chuẩn hoá RIÊNG:
        // trần "tối đa 4 câu hỏi một lượt" và việc hạ lượt-gộp-một-câu về đường một-câu sống trong
        // Normalize. Bỏ bước này thì các model tốt (đường mặc định) là các model KHÔNG bị chặn.
        var parsedReply = structuredReply != null
            ? _replyParser.Normalize(structuredReply)
            : _replyParser.Parse(callResult.Content);

        ApplyModelReply(draft, parsedReply);
        ApplyRepeatedQuestionBrake(draft, parsedReply, turn);
        ApplyReadinessGate(draft, turn);
        await BuildInterviewTableAsync(draft, parsedReply, turn, ba, model, cancellationToken);
        ApplyColumnReadbackShape(draft, turn);
        ApplySourceRequestTurn(draft, turn);
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
            var (message, openEnded) = BuildFollowUpAfterRepeat(turn.Project.RequirementCoverageMap, turn.Recent);
            draft.Reply = message;
            draft.OpenEnded = openEnded;
        }
    }

    /// <summary>Lượt model trả về, đã chuẩn hoá — điểm xuất phát của mọi chốt chặn phía sau.</summary>
    private static void ApplyModelReply(BAChatTurnDraft draft, BAChatReply parsedReply)
    {
        // Lượt phải gửi lại mà bỏ ảnh có kèm một dòng dặn dò NỘI BỘ cho model; model yếu hay chép
        // nguyên văn dòng đó vào câu trả lời. Dọn trước khi nó thành một lượt hội thoại.
        draft.Reply = string.IsNullOrWhiteSpace(parsedReply.Message)
            ? "Đã ghi nhận. Bạn có thể bổ sung thêm yêu cầu, hoặc bấm \"Write Requirement\" để tạo tài liệu."
            : EndpointQuirks.StripInternalNotices(parsedReply.Message);

        // Lưu suggestions tách riêng (JSON) để UI render chip; chỉ set khi thực sự có gợi ý.
        if (parsedReply.Suggestions.Count > 0)
        {
            draft.SuggestionsJson = JsonSerializer.Serialize(parsedReply.Suggestions);
            draft.SuggestionsMultiSelect = parsedReply.MultiSelect;
        }

        // Normalize đã đảm bảo OpenEnded ⇒ Suggestions rỗng, nên hai nhánh này loại trừ nhau.
        draft.OpenEnded = parsedReply.OpenEnded;

        // Lượt hỏi GỘP (2–4 câu độc lập): Normalize đã đảm bảo hoặc có Questions, hoặc có
        // Suggestions — không bao giờ cả hai.
        draft.Questions = parsedReply.Questions;
    }

    /// <summary>
    /// PHANH CHỐNG HỎI LẠI (tất định). Prompt đã cấm phát lại câu cũ, nhưng bản đồ bao phủ — thứ dẫn dắt
    /// lượt hỏi — chỉ có độ phân giải theo NHÓM: một dòng chưa đạt chuẩn [RÕ] (hoặc một lượt chắt lọc bản
    /// đồ hỏng, giữ nguyên bản cũ) là đủ để model phát lại nguyên văn cả cụm câu hỏi của lượt trước, kèm
    /// chip gợi ý chính là câu trả lời người dùng vừa gõ. Ở đây câu trùng bị LOẠI khỏi lượt trả lời trước
    /// khi nó kịp lên màn hình.
    /// </summary>
    private void ApplyRepeatedQuestionBrake(BAChatTurnDraft draft, BAChatReply parsedReply, TurnContext turn)
    {
        var askedKeys = AskedQuestionHistory.Keys(turn.AskedBefore);
        var reopenedGroups = AskedQuestionHistory.ReopenedGroups(CoverageMapParser.Parse(turn.Project.RequirementCoverageMap));
        // …và sổ thứ hai: các CHIP đã bày ở một lượt chọn-nhiều mà người dùng không chọn. Một chip bị
        // bỏ là một câu trả lời ("cái này thì không"), nhưng nó không nằm trong sổ câu hỏi nên một câu
        // có/không hỏi riêng đúng chip đó lọt qua phanh trên — xem AskedQuestionHistory.DeclinedChipKeys.
        var declinedChips = AskedQuestionHistory.DeclinedChipKeys(turn.Recent);

        if (draft.Questions.Count > 0)
        {
            var kept = draft.Questions
                .Where(q => AskedQuestionHistory.IsExempt(q, reopenedGroups)
                            || (!AskedQuestionHistory.IsRepeat(q.Question, askedKeys)
                                && !AskedQuestionHistory.AsksAboutDeclinedChip(q.Question, declinedChips)))
                .ToList();

            if (kept.Count == draft.Questions.Count)
                return;

            // Câu dẫn của lượt gộp thường tự đếm ("dưới đây là 4 câu xác nhận") nên bỏ bớt câu là
            // nó nói sai — thay bằng câu dẫn trung tính. Còn đúng một câu thì để Message rỗng cho
            // Normalize nâng chính câu hỏi lên làm nội dung lượt (đường một-câu).
            var trimmed = _replyParser.Normalize(new BAChatReply
            {
                Message = kept.Count >= 2 ? "Cảm ơn anh/chị. Mình hỏi thêm mấy điểm sau nhé:" : string.Empty,
                Questions = kept
            });

            if (kept.Count == 0)
            {
                // Không còn câu nào MỚI để hỏi: lượt này lẽ ra rỗng. Thay bằng bước kế tiếp TẤT
                // ĐỊNH suy từ bản đồ (hỏi đúng nhóm còn thiếu, hoặc mời bấm nút khi bản đồ đã đủ)
                // — im lặng hoặc để nguyên câu dẫn cụt đều tệ hơn. Câu đó không có chip, nên cờ
                // "câu mở" đi theo nó để ô nhập nhận vai chỗ trả lời.
                var (message, openEnded) = BuildFollowUpAfterRepeat(turn.Project.RequirementCoverageMap, turn.Recent);
                draft.Replace(message, openEnded);
            }
            else
            {
                draft.Reply = trimmed.Message;
                draft.SuggestionsJson = trimmed.Suggestions.Count > 0 ? JsonSerializer.Serialize(trimmed.Suggestions) : null;
                draft.SuggestionsMultiSelect = trimmed.MultiSelect && trimmed.Suggestions.Count > 0;
                draft.OpenEnded = trimmed.OpenEnded;
            }

            draft.Questions = trimmed.Questions;
            return;
        }

        // "Có chip HOẶC là câu mở" = lượt này thật sự đang HỎI. Trước đây vế đầu là đủ vì mọi câu hỏi
        // đều bắt buộc kèm chip; từ khi câu mở được phép bỏ chip, chỉ xét chip là để lọt đúng loại câu
        // đắt nhất (xin lời kể) ra khỏi phanh chống hỏi lại.
        if ((parsedReply.Suggestions.Count > 0 || parsedReply.OpenEnded)
            && !RequirementReadinessGate.IsWriteRequirementInvite(draft.Reply)
            && (AskedQuestionHistory.IsRepeat(draft.Reply, askedKeys)
                || AskedQuestionHistory.AsksAboutDeclinedChip(draft.Reply, declinedChips)))
        {
            // Lượt hỏi MỘT câu, và chính câu đó đã hỏi rồi (Message chở câu hỏi ở đường này).
            var (message, openEnded) = BuildFollowUpAfterRepeat(turn.Project.RequirementCoverageMap, turn.Recent);
            draft.Replace(message, openEnded);
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
        if (!RequirementReadinessGate.IsWriteRequirementInvite(draft.Reply))
            return;

        // `Recent` đi kèm để cổng không phát lại đúng câu chặn nó vừa phát: câu của cổng không có chip nên
        // phanh chống hỏi lại dùng chung không thấy nó — xem RequirementReadinessGate.
        var readiness = RequirementReadinessGate.Evaluate(turn.Project.RequirementCoverageMap, turn.Recent);
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

        // Lời mời ĐƯỢC GIỮ: lượt này là lời mời bấm nút, không phải lượt hỏi. Một lời mời kèm thẻ hỏi là
        // tự mâu thuẫn ("không còn gì để hỏi" + 3 câu hỏi), và ở đúng lượt mà cổng vừa mở — người dùng sẽ
        // trả lời thẻ đó rồi tự hỏi vì sao mình vẫn chưa được viết. Lời mời cũng không phải câu hỏi ⇒
        // không mời người dùng "kể tự do" ở ô nhập.
        draft.Questions = new List<BAChatQuestion>();
        draft.OpenEnded = false;
    }

    /// <summary>
    /// Dựng BẢNG của cổng đang mở từ đề xuất model. Chỉ chạy khi cổng đã mở (xem
    /// <see cref="InterviewTableGate"/>), nên không lượt nào giữa buổi bị thay bằng một bảng dựng trên
    /// phạm vi mới có một nửa.
    ///
    /// <para>
    /// Model không trả bảng dùng được (structured output tắt, hoặc mọi dòng đều trỏ vào màn hình không có
    /// trong phạm vi) ⇒ FAIL-OPEN: lượt chạy y như một lượt chat thường và cổng sẽ mở lại ở lượt sau. Một
    /// lượt hỏi thừa rẻ hơn nhiều so với một lượt câm.
    /// </para>
    /// </summary>
    private async Task BuildInterviewTableAsync(
        BAChatTurnDraft draft, BAChatReply parsedReply, TurnContext turn,
        Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var project = turn.Project;
        switch (turn.Table)
        {
            case InterviewTableKind.PermissionMatrix:
                draft.PermissionMatrix = PermissionMatrixBuilder.Build(parsedReply.PermissionMatrix, turn.EffectiveScreens);
                if (draft.PermissionMatrix.Count > 0)
                    draft.TakeOverForTable(PermissionMatrixIntro, parsedReply.Message);
                break;

            case InterviewTableKind.FlowMap:
                draft.FlowMap = FlowMapBuilder.Build(parsedReply.FlowMap);
                if (draft.FlowMap.Count > 0)
                    draft.TakeOverForTable(FlowMapIntro, parsedReply.Message);
                break;

            case InterviewTableKind.ScreenScope:
                // GIEO bằng chính bảng đang lưu. Cổng này mở lại được khi có mục mới lộ ra sau lúc
                // chốt — xem ScreenScopeGate — và Build dựng bảng từ đề xuất TƯƠI của model, nên không
                // gieo thì lần bày lại thay sạch phần người dùng đã tự tay rà bằng bản model vừa đoán
                // lại. Hạt giống là tham số RIÊNG: chỉ nó được chở cờ đã-chốt qua, và chỉ dòng CHƯA
                // CHỐT của nó mới cho model lấp thêm vào (xem Build).
                draft.ScreenScopeMap = ScreenScopeMapBuilder.Build(
                    ScreenScopeMapBuilder.SeedRows(project.ScreenScopeMap),
                    parsedReply.ScreenScopeMap,
                    turn.EffectiveScreens);
                if (draft.ScreenScopeMap.Count > 0)
                {
                    // Lượt BÀY LẠI ép dùng câu dẫn của hệ thống (force) thay vì câu của model. Đây là
                    // ngoại lệ DUY NHẤT của luật "câu dẫn model thắng", và nó cần thiết vì model không
                    // có cách nào biết lượt này khác lượt trước: nó nhận cùng một khối lệnh bày bảng và
                    // viết ra đúng một câu như lần đầu ("anh/chị rà soát bảng màn hình dưới đây rồi
                    // bấm…"), tức lời mời rà lại TOÀN BỘ một bảng mà người dùng vừa chốt. Thứ phải nói
                    // ra — giữ nguyên phần đã chốt, chỉ thêm màn hình nào — là dữ kiện của CƠ CHẾ
                    // (SeedRows + NewScreens), nên nó phải do cơ chế viết.
                    if (turn.ReshowScreenScope)
                        draft.TakeOverForTable(ScreenScopeReshowIntro(turn.PendingScreens, turn.PendingFunctions), parsedReply.Message, force: true);
                    else
                        draft.TakeOverForTable(ScreenScopeIntro, parsedReply.Message);

                    await CoverFlowStepsAsync(draft, turn, ba, model, cancellationToken);
                }
                break;

            case InterviewTableKind.EntityMap:
                draft.EntityMap = EntityMapBuilder.Build(parsedReply.EntityMap, ConfirmedColumnNames(turn.Sources));
                if (draft.EntityMap.Count > 0)
                    draft.TakeOverForTable(EntityMapIntro, parsedReply.Message);
                break;

            case InterviewTableKind.ReportMap:
                // Dòng do MODEL đề xuất (không có bảng nào gieo ra được một báo cáo), nhưng ô "lấy số
                // từ" thì phải trỏ về bảng đối tượng đã chốt — đó là mối nối duy nhất giữ cho bước sinh
                // spec không tự nghĩ ra một nguồn dữ liệu cho báo cáo.
                draft.ReportMap = ReportMapBuilder.Build(parsedReply.ReportMap, turn.EntityNames);
                if (draft.ReportMap.Count > 0)
                    draft.TakeOverForTable(ReportMapIntro, parsedReply.Message);
                break;

            case InterviewTableKind.NotificationMap:
                // Dòng do CƠ CHẾ gieo, không do model liệt kê: model chỉ điền người nhận vào các dòng
                // có sẵn. Một sự kiện model quên nêu vẫn có mặt ở trạng thái chưa chọn người nhận —
                // im lặng bỏ nó đi là biến "chưa hỏi" thành "không báo cho ai".
                draft.NotificationMap = NotificationMapBuilder.Build(
                    parsedReply.NotificationMap, turn.NotificationSeedRows, turn.RecipientOptions);
                if (draft.NotificationMap.Count > 0)
                    draft.TakeOverForTable(NotificationMapIntro, parsedReply.Message);
                break;
        }
    }

    /// <summary>
    /// Phép kiểm TẤT ĐỊNH của mối nối luồng ⇄ màn hình, chạy bằng code chứ không bằng một lời gọi LLM:
    /// hai bảng đọc riêng đều "đạt", chỗ hỏng nằm ở chỗ nối. Xem
    /// <see cref="ScreenScopeMapBuilder.UncoveredActions"/>.
    ///
    /// <para>
    /// Bắt được lỗ hổng thì BA TỰ LẤP, không hỏi ngược người dùng. Việc ánh xạ một bước nghiệp vụ sang một
    /// chức năng trên một màn hình là phần việc họ đi thuê BA để làm; dòng nhắc dưới bảng ở lại làm chỗ rơi
    /// cuối cùng cho những bước mà chính BA cũng không xếp nổi — đúng ca duy nhất đáng hỏi. Xem
    /// <see cref="ScreenStepPlacementService"/>.
    /// </para>
    /// </summary>
    private async Task CoverFlowStepsAsync(BAChatTurnDraft draft, TurnContext turn, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var uncovered = ScreenScopeMapBuilder.UncoveredActions(draft.ScreenScopeMap, turn.Project.FlowMap);
        draft.UncoveredFlowSteps = uncovered;
        if (uncovered.Count == 0)
            return;

        var screensBefore = draft.ScreenScopeMap.Select(r => r.Screen).ToList();
        draft.ScreenScopeMap = await _stepPlacement.PlaceAsync(
            turn.ProjectId, draft.ScreenScopeMap, uncovered, ba, model, cancellationToken);

        var stillUncovered = ScreenScopeMapBuilder.UncoveredActions(draft.ScreenScopeMap, turn.Project.FlowMap);
        var placed = uncovered.Except(stillUncovered, StringComparer.OrdinalIgnoreCase).ToList();
        draft.UncoveredFlowSteps = stillUncovered;

        var notice = ScreenScopePlacementNotice(screensBefore, draft.ScreenScopeMap, placed);
        if (notice != null)
            draft.Reply = draft.Reply.TrimEnd() + notice;
    }

    /// <summary>
    /// Lượt KỂ LẠI file: chỗ trả lời là hai chip xác nhận, nên không được có thẻ hỏi gộp — thẻ hỏi và chip
    /// loại trừ nhau trên màn hình (<see cref="BAChatReplyParser.Normalize"/>), và một thẻ hỏi ở đây nuốt
    /// mất đúng thứ lượt này cần lấy: bản đọc rốt cuộc đúng hay sai. Các câu hỏi khai thác quay lại từ lượt
    /// sau.
    /// </summary>
    private static void ApplyColumnReadbackShape(BAChatTurnDraft draft, TurnContext turn)
    {
        if (!turn.ColumnReadbackTurn)
            return;

        draft.Questions = new List<BAChatQuestion>();
        // …và bỏ thẻ hỏi thì phải TRẢ LẠI chip: chính vì model kèm thẻ hỏi mà Normalize đã dọn
        // sạch Suggestions của lượt. Để nguyên là bày ra một câu hỏi đóng KHÔNG CÓ nút trả lời —
        // đúng lỗi mà lượt đọc bảng tính đã vấp một lần, chỉ khác chỗ phát sinh.
        if (string.IsNullOrEmpty(draft.SuggestionsJson))
            draft.SetFallbackSuggestions(SourceReadbackSuggestions);
    }

    /// <summary>
    /// LƯỢT XIN FILE — người dùng vừa nhắc tới một file/bảng tính họ đang dùng mà dự án chưa có tài liệu
    /// nguồn nào. Luật "xin file NGAY TẠI LƯỢT ĐÓ" nằm trong prompt từ lâu và vẫn trượt im lặng (ca thật:
    /// JD Libary 5, người dùng nhắc hai file excel ở lượt 3 và 5, BA không xin lần nào trong 26 lượt) —
    /// nên nó phải là một chốt chặn tất định như mọi luật đắt khác.
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
    /// CUỐI thật sự nhắc tới một vật mang dữ liệu, chưa lượt BA nào xin file (giục lần hai là phí lượt), và
    /// lượt này không phải lượt bày BẢNG (bảng là chỗ trả lời duy nhất của nó).
    /// </para>
    /// </summary>
    private static void ApplySourceRequestTurn(BAChatTurnDraft draft, TurnContext turn)
    {
        if (turn.Sources.Count == 0
            && turn.LastUserIndex >= 0
            && SourceRequestTurn.MentionsExistingSource(turn.Recent[turn.LastUserIndex].Message)
            && !SourceRequestTurn.Looks(draft.Reply)
            && !turn.Recent.Any(c => ConversationTurnRenderer.IsAssistant(c) && SourceRequestTurn.Looks(c.Message))
            && !draft.CarriesTable)
        {
            draft.Replace(SourceRequestTurn.Message, openEnded: true);
        }
    }

    /// <summary>
    /// NHỊP TÓM TẮT KIỂM CHỨNG mà quên chip. Prompt kê sẵn bộ hai chip cho lượt này (["Đúng rồi, tiếp tục",
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
            && LooksVerificationSummary(draft.Reply))
        {
            draft.SetFallbackSuggestions(SummaryCheckSuggestions);
        }
    }

    /// <summary>Lưu lượt assistant vừa nắn xong rồi trả bản CHỐT cho endpoint streaming render tại chỗ.</summary>
    private async Task<BAChatTurnResult> SaveTurnAsync(TurnContext turn, Agent ba, BAChatTurnDraft draft, CancellationToken cancellationToken)
    {
        var project = turn.Project;
        var questionsJson = draft.Questions.Count > 0 ? JsonSerializer.Serialize(draft.Questions) : null;
        var permissionMatrixJson = draft.PermissionMatrix.Count > 0 ? JsonSerializer.Serialize(draft.PermissionMatrix) : null;
        var flowMapJson = draft.FlowMap.Count > 0 ? JsonSerializer.Serialize(draft.FlowMap) : null;
        var screenScopeMapJson = draft.ScreenScopeMap.Count > 0 ? JsonSerializer.Serialize(draft.ScreenScopeMap) : null;
        var entityMapJson = draft.EntityMap.Count > 0 ? JsonSerializer.Serialize(draft.EntityMap) : null;
        var reportMapJson = draft.ReportMap.Count > 0 ? JsonSerializer.Serialize(draft.ReportMap) : null;
        var notificationMapJson = draft.NotificationMap.Count > 0 ? JsonSerializer.Serialize(draft.NotificationMap) : null;
        // ĐÓNG DẤU "cổng readiness đã pass ở lượt này" lên chính lượt sắp lưu — thứ mà bước soạn tài liệu
        // đọc để biết có được bỏ qua lần xét lại hay không (xem AgentConversation.ReadinessVerified).
        //
        // Suy từ bản CHỐT (`draft.Reply`) chứ không từ nhánh nào phía trên, và đó là điểm mấu chốt: nội
        // dung lượt còn bị nhiều chốt chặn sau đó viết lại — lượt có BẢNG thay lời mời bằng câu dẫn của
        // bảng (TakeOverForTable), lượt câm bị thay bằng bước kế tất định (có thể LÀ lời mời khi bản đồ đã
        // đủ). Đặt cờ ở nhánh "lời mời được giữ" rồi mang xuống đây là dựng lại đúng kiểu vênh mà cột này
        // sinh ra để dẹp: cờ nói một đằng, lượt được lưu một nẻo. Bản đồ dùng để xét là bản đã gộp ở ĐẦU
        // lượt này — cùng dữ liệu mà cổng readiness đã xét, nên hai chỗ không thể lệch nhau.
        var readinessVerified = RequirementReadinessGate.IsReadinessVerifiedTurn(draft.Reply, project.RequirementCoverageMap);

        await _conversationLog.AppendAsync(turn.ProjectId, ba.Id, "assistant", draft.Reply, draft.SuggestionsJson, draft.SuggestionsMultiSelect, questionsJson: questionsJson, permissionMatrixJson: permissionMatrixJson, flowMapJson: flowMapJson, screenScopeMapJson: screenScopeMapJson, entityMapJson: entityMapJson, reportMapJson: reportMapJson, notificationMapJson: notificationMapJson, readinessVerified: readinessVerified, cancellationToken: cancellationToken);

        // Trả bản CHỐT (đúng bản vừa lưu) để endpoint streaming render tại chỗ — bản preview đã stream
        // có thể khác (vd lời mời bị gate thay bằng câu hỏi), client luôn thay preview bằng bản này.
        return new BAChatTurnResult
        {
            Status = ChatWithBAResult.Ok,
            Reply = draft.Reply,
            Suggestions = string.IsNullOrEmpty(draft.SuggestionsJson)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(draft.SuggestionsJson) ?? new List<string>(),
            InvitesWriteRequirement = RequirementReadinessGate.IsWriteRequirementInvite(draft.Reply),
            SuggestionsMultiSelect = draft.SuggestionsMultiSelect,
            OpenEnded = draft.OpenEnded,
            Questions = draft.Questions,
            // Bản đồ ở thời điểm này đã gộp tới lượt user mới nhất (cập nhật đầu lượt); lượt BA vừa trả
            // lời sẽ được gộp ở lượt sau — đủ tươi cho panel tiến độ.
            Coverage = CoverageMapParser.Parse(project.RequirementCoverageMap).ToList(),
            // Cùng cổng tất định, cùng bản đồ — chỉ khác câu hỏi: "đã đủ vốn chưa" thay vì "lượt này có
            // phải lời mời không". UI cần cả hai vì sau khi bản Brief đã tồn tại, một lượt BA không mời
            // (BA hỏi thêm một câu) không được phép cắt mất đường soạn lại bản Brief đang cũ dần.
            CoverageReady = RequirementReadinessGate.Evaluate(project.RequirementCoverageMap).Ready,
            PermissionMatrix = draft.PermissionMatrix,
            FlowMap = draft.FlowMap,
            ScreenScopeMap = draft.ScreenScopeMap,
            EntityMap = draft.EntityMap,
            ReportMap = draft.ReportMap,
            // Cùng luật với RecipientOptions ngay dưới: mục chọn chỉ có nghĩa khi lượt này thật sự bày bảng.
            ReportEntityOptions = draft.ReportMap.Count > 0 ? turn.EntityNames : new List<string>(),
            NotificationMap = draft.NotificationMap,
            // Danh sách chọn chỉ có nghĩa khi lượt này thật sự bày bảng: client dựng ô chọn từ đây, và
            // server đối chiếu đúng bộ này lúc gửi lên.
            RecipientOptions = draft.NotificationMap.Count > 0 ? turn.RecipientOptions : new List<string>(),
            UncoveredFlowSteps = draft.UncoveredFlowSteps,
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
    /// Tên các cột người dùng ĐÃ TÍCH ở mọi bảng cột đã chốt của dự án. Bảng đối tượng dùng nó để khỏi bày
    /// lại đúng những thông tin họ vừa tự tay tích — bắt duyệt lần hai chính là hình dạng vòng lặp câu hỏi
    /// chết mà repo đã phải dựng lưới một lần.
    /// </summary>
    private static List<string> ConfirmedColumnNames(IEnumerable<ProjectSourceFile> sources)
        => sources
            .SelectMany(s => SourceColumnMapBuilder.Parse(s.ColumnMap))
            .Where(c => c.Used && !string.IsNullOrWhiteSpace(c.Column))
            .Select(c => c.Column.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Bước kế tiếp TẤT ĐỊNH khi mọi câu hỏi của lượt vừa rồi đều là câu đã hỏi: hỏi đúng nhóm mà bản đồ
    /// bao phủ còn ghi thiếu, hoặc — khi bản đồ đã đủ theo cùng cổng readiness dùng ở mọi nơi khác — mời
    /// bấm "Write Requirement". Không bao giờ trả về lượt rỗng: một lượt câm sau khi người dùng vừa trả
    /// lời còn khó hiểu hơn cả việc bị hỏi lại.
    /// </summary>
    private static (string Message, bool OpenEnded) BuildFollowUpAfterRepeat(
        string? coverageMap, IReadOnlyList<AgentConversation> turns)
    {
        // Đường này là chỗ câu chặn của cổng dễ lặp nhất: lượt của BA toàn câu đã hỏi thì lượt nào cũng rơi
        // vào đây, và nếu bản đồ chưa nhúc nhích thì cổng lại chọn đúng nhóm cũ. `turns` cho cổng đổi nhóm.
        var readiness = RequirementReadinessGate.Evaluate(coverageMap, turns);
        if (!readiness.Ready)
        {
            return (string.IsNullOrWhiteSpace(readiness.Message)
                ? "Mình cần làm rõ thêm vài thông tin trước khi viết tài liệu. Bạn bổ sung giúp nhé."
                : readiness.Message, readiness.OpenEnded);
        }

        // Bản đồ đã đủ ⇒ lời mời này đi qua đúng cổng mà nhánh dưới sẽ xét lại, nên không thể là lời mời
        // sớm. Không phải câu hỏi nên cũng không mở ô nhập: hành động duy nhất lúc này là bấm nút thật.
        return ("Mình đã ghi nhận đủ thông tin cần thiết và không còn câu hỏi nào mới. "
                + "Nếu anh/chị không còn gì bổ sung, bấm nút \"Write Requirement\" để mình tạo tài liệu nhé.",
            false);
    }

    /// <summary>
    /// Gộp lượt chat mới vào "triển vọng phỏng vấn" (điểm cần làm rõ + màn hình dự kiến + ví dụ tính thử
    /// đã xác nhận) rồi trả bản hiện hành. Gọi ở HẬU KỲ lượt chat (sau frame done) để lời gọi LLM này
    /// không cộng vào độ chờ cảm nhận. Fail-open toàn phần.
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
            var sourceContents = _sourceContextBuilder.Build(sources, model);
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

            var scope = BuildSourceAckScope(sources, attachments);

            // Ghi chú người dùng gõ cạnh ảnh (nếu có) → BA đọc đúng trọng tâm thay vì tóm tắt chung chung.
            // Câu này GỌI TÊN đúng các file vừa gửi: câu cũ ("đây là các tài liệu nguồn tôi vừa đính kèm")
            // đứng trước text của mọi nguồn nên nó khai man rằng file cũ cũng vừa được gửi.
            var justSentList = string.Join(", ", scope.JustSentFiles);
            var promptText = string.IsNullOrEmpty(trimmedNote)
                ? $"Tôi vừa đính kèm: {justSentList}. Bạn đọc kỹ và kể lại cụ thể những gì rút được từ đó để tôi xác nhận nhé."
                : $"Tôi vừa đính kèm: {justSentList}, kèm ghi chú của tôi: \"{trimmedNote}\". Bạn đọc kỹ và kể lại cụ thể những gì rút được từ đó để tôi xác nhận nhé.";

            var userContent = new List<AIContent> { new TextContent(promptText) };
            userContent.AddRange(sourceContents.Contents);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/source-ack.v3.md")),
                new(ChatRole.System, BASourceAckPrompt.TurnShape(scope.PendingColumnFiles, scope.JustSentFiles, scope.EarlierFiles)),
                new(ChatRole.User, userContent)
            };

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
            if (scope.PendingColumnFiles.Count > 0 && columnMapJson == null)
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
                model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BASourceAck"),
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
        // KHÔNG echo sơ đồ luồng nữa: trường đã ra khỏi schema trả lời (xem BAChatReply), nên dựng lại một
        // lượt cũ có nó là dạy model đúng cái format vừa gỡ. Luồng mà người dùng đã duyệt quay lại ngữ cảnh
        // ở khối "bảng đã chốt" (FlowMapBuilder.RenderConfirmedBlock), đầy đủ hơn hẳn.
        // Echo cả các câu hỏi của lượt GỘP: đây là chỗ model học rằng gộp là hợp lệ VÀ học nhịp gộp của
        // chính nó. Bỏ trường này thì mọi lượt cũ trông như lượt một-câu và model trượt về một-câu-một-lượt
        // sau vài vòng — đúng kiểu trượt format mà hàm này sinh ra để chặn.
        var questions = ConversationTurnRenderer.ParseQuestions(c.Questions)
            .Select(q => new { group = q.Group, question = q.Question, suggestions = q.Suggestions, multiSelect = q.MultiSelect, openEnded = q.OpenEnded });
        return JsonSerializer.Serialize(new { message = c.Message, suggestions, multiSelect = c.SuggestionsMultiSelect, questions, ready });
    }
}
