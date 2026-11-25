using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Localization;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Requirements.Templates;
using ICOGenerator.Services.Organization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ICOGenerator.Tests;

namespace ICOGenerator.Tests.Requirements;

// Cổng readiness TẤT ĐỊNH: ready suy thẳng từ bản đồ bao phủ (mọi dòng áp dụng [RÕ] ⇔ cho phép
// "Write Requirement") — bản đồ là nguồn chân lý duy nhất nên panel tiến độ, lời mời của BA và cổng
// lúc bấm nút không thể vênh nhau. Các test chốt: (1) Evaluate thuần — đủ/thiếu/bản đồ trống
// (fail-closed); (2) lượt chat — lời mời chỉ được giữ khi bản đồ đủ, ngược lại bị thay bằng câu hỏi
// dựng sẵn nêu đúng nhóm thiếu; (3) bước sinh tài liệu — lượt cuối là lời mời đã duyệt thì đi thẳng,
// còn lại gộp nốt bản đồ rồi xét tất định (KHÔNG lời gọi LLM chấm readiness nào ở bất kỳ đâu).
public class RequirementReadinessGateTests : IDisposable
{
    private const string InviteMessage = "Mình đã đủ thông tin. Nếu không còn gì bổ sung, vui lòng bấm nút \"Write Requirement\" để tạo tài liệu.";

    // Một dòng bullet chở cả hai nửa mà production lưu ở hai cột — bản đồ và danh sách câu hỏi. Cổng đọc
    // cả hai, nên test cũng dựng cả hai từ cùng một chỗ.
    private const string BulletsAllClear = """
        - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép.
        - ★ Đối tượng người dùng & vai trò: [RÕ] Nhân viên nộp, quản lý duyệt.
        - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] người dùng không cần.
        """;

    private const string BulletsMissingRules = """
        - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép.
        - Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Trừ quỹ phép năm; còn thiếu: hạn mức ngày phép.
        """;

    private static readonly string MapAllClear = CoverageMapFixture.Map(BulletsAllClear);
    private static readonly string MapMissingRules = CoverageMapFixture.Map(BulletsMissingRules);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public RequirementReadinessGateTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app nghỉ phép" });
        db.SaveChanges();
    }

    // ---------- Evaluate thuần (không DB, không LLM) ----------

    // Bản của DỰ ÁN thêm đúng một vế: bảng luồng phải đã chốt. Bản đồ đủ mà bảng luồng chưa được bày/gửi
    // (model quên bày bảng ở lượt cổng mở) ⇒ CHƯA sẵn sàng — bước "Write Requirement" dựng bản thiết kế UX từ
    // chính các bước luồng đã chốt. Trước đây ca này được chặn gián tiếp bởi nhóm «Phân quyền» (chỉ [RÕ] khi
    // đã có bảng phân quyền, bảng đó chờ bản thiết kế, bản thiết kế chờ bảng luồng); nhóm đó đã bỏ.
    [Fact]
    public void EvaluateProject_WaitsForTheFlowTable_EvenWhenTheMapIsClear()
    {
        const string bullets = """
            - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép.
            - ★ Đối tượng người dùng & vai trò: [RÕ] Nhân viên nộp, quản lý duyệt.
            - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Nộp đơn, quản lý duyệt.
            - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Từ chối thì nhân viên sửa lại.
            """;
        var project = new Project { RequirementCoverageMap = CoverageMapFixture.Map(bullets) };

        var pending = RequirementReadinessGate.Evaluate(project, Array.Empty<OpenQuestionEntry>());
        Assert.False(pending.Ready);
        Assert.True(pending.OpenEnded);
        Assert.EndsWith("?", pending.Message, StringComparison.Ordinal);
        Assert.False(RequirementReadinessGate.IsReady(project));
        Assert.True(RequirementReadinessGate.IsReady(project.RequirementCoverageMap)); // bản thuần chỉ xét bản đồ

        project.FlowMap = """[{"name":"Nghỉ phép","kind":"luồng chính","role":"Nhân viên","steps":[{"actor":"Nhân viên","action":"Nộp đơn","outcome":"Chờ duyệt","included":true}]}]""";
        Assert.True(RequirementReadinessGate.IsReady(project));
    }

    [Fact]
    public void Evaluate_AllApplicableClear_IsReady()
    {
        var readiness = RequirementReadinessGate.Evaluate(MapAllClear, CoverageMapFixture.Questions(BulletsAllClear));

        Assert.True(readiness.Ready);
    }

    [Fact]
    public void Evaluate_PartialLine_NotReady_QuestionAsksTheGapWithoutNamingTheGroup()
    {
        var readiness = RequirementReadinessGate.Evaluate(MapMissingRules, CoverageMapFixture.Questions(BulletsMissingRules));

        Assert.False(readiness.Ready);
        // Câu hỏi dựng sẵn phải hỏi ĐÚNG nội dung phần "còn thiếu" distiller đã ghi — thành câu, không bê
        // nguyên cụm bookkeeping "còn thiếu:" ra cho người dùng đọc, và KHÔNG đọc nhãn nhóm của bản đồ ra
        // màn hình. Không được chứa "Write Requirement" (chuỗi đó là tín hiệu làm nổi nút trên UI).
        Assert.Contains("hạn mức ngày phép", readiness.Message);
        Assert.DoesNotContain("Quy tắc nghiệp vụ & ràng buộc", readiness.Message);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
        Assert.DoesNotContain("Write Requirement", readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ĐỪNG TÌM test "mẩu còn thiếu RỖNG NGHĨA không lên màn hình" ở đây: cổng từng tự lọc ("các quy tắc khác
    // (nếu có)", "thông tin bổ sung"…) bằng một bảng cụm tiếng Việt ở đường ĐỌC. Việc đó nay do lượt trọng
    // tài làm ở đường GHI (InterviewJudge + CoverageQuestionGuard) — xem InterviewJudgeTests.
    //
    // Cổng phát NGUYÊN VĂN câu hỏi đang lưu, kể cả khi nó kết bằng chữ "khác".
    [Fact]
    public void Evaluate_AGapCarryingRealContent_IsStillAsked()
    {
        const string bullets = "- Vòng đời & trạng thái: [MỘT PHẦN] JD có trạng thái submit, verify, approve. "
                  + "còn thiếu: tên chính thức của các trạng thái và điều kiện chuyển giữa chúng";

        var readiness = RequirementReadinessGate.Evaluate(
            CoverageMapFixture.Map(bullets), CoverageMapFixture.Questions(bullets));

        Assert.Contains("tên chính thức của các trạng thái", readiness.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CoreGroupAskedBeforeSecondary()
    {
        const string bullets = """
            - Quy mô sử dụng: [CHƯA HỎI]
            - ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] còn thiếu: luồng duyệt.
            """;

        var readiness = RequirementReadinessGate.Evaluate(
            CoverageMapFixture.Map(bullets), CoverageMapFixture.Questions(bullets));

        Assert.False(readiness.Ready);
        // Dòng ★ cốt lõi được hỏi trước dù đứng sau trong bản đồ — và chỉ hỏi MỘT chỗ, dòng phụ để dành
        // lượt sau chứ không bị hỏi dồn trong cùng lượt.
        Assert.Contains("luồng duyệt", readiness.Message);
        Assert.DoesNotContain(CoverageGroupOpeners.Find(CoverageGroups.KeyOf("Quy mô sử dụng"))!, readiness.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chưa có bản đồ nào cả")] // không parse được dòng nào — như chưa có bản đồ.
    public void Evaluate_MissingMap_FailsClosed(string? map)
    {
        var readiness = RequirementReadinessGate.Evaluate(map, Array.Empty<OpenQuestionEntry>());

        Assert.False(readiness.Ready);
        Assert.False(string.IsNullOrWhiteSpace(readiness.Message));
    }

    [Fact]
    public void Evaluate_AllNotApplicable_IsBrokenMap_NotReady()
    {
        var readiness = RequirementReadinessGate.Evaluate(
            CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [KHÔNG ÁP DỤNG] ?"), Array.Empty<OpenQuestionEntry>());

        Assert.False(readiness.Ready);
    }

    // ---------- Prompt phải DẠY cờ, vì cờ nay là công tắc ----------
    //
    // Cùng khuôn với SummaryCheckFlagRuleTests: từ khi cổng đọc `ready` thay cho mặt chữ, một prompt quên
    // dạy cờ này là một cái nút không bao giờ sáng — và không có test nào khác đỏ vì chuyện đó.
    [Fact]
    public void Prompt_TeachesThatReadyIsTheSwitch_AndThatAReadyTurnIsNotStillAsking()
    {
        var prompt = PromptFixture.Read("BusinessAnalyst/requirement-chat.v4.md");

        Assert.Contains("CÔNG TẮC", prompt, StringComparison.Ordinal);
        // …và dạy luôn hậu quả của việc khai cờ trên một lượt vẫn đang hỏi: chốt chặn ở ApplyReadinessGate
        // hạ cờ xuống (xem ChatAsync_ReadyFlagOnATurnThatIsStillAsking_IsDropped).
        Assert.Contains("hạ cờ", prompt, StringComparison.Ordinal);
    }

    // ---------- Lời mời được CỔNG nói thay: lượt đó không có bong bóng riêng ----------
    //
    // Ca thật trên màn hình: người dùng vừa gửi bảng thông báo, BA đáp *"Cảm ơn anh/chị đã xác nhận bảng
    // thông báo. Mình đã nắm đầy đủ các thông tin cần thiết… anh/chị bấm nút Write Requirement để mình
    // soạn tài liệu nhé."* — rồi NGAY DƯỚI là thẻ cổng nói lại đúng thế ("mình đã thu thập đủ thông tin
    // để soạn bản mô tả sản phẩm") kèm cái nút thật. Hai khung liền nhau nói một điều, và khung có nút mới là khung trả lời được; lời mời còn trỏ
    // tới "bản tóm tắt trên" trong khi thứ đứng trên nó là một cái bảng. Cùng luật đã áp cho câu dẫn của
    // lượt hỏi GỘP. Hai chỗ vẽ (Index.cshtml lúc tải trang, requirements.js ở frame done) đọc CHUNG hàm
    // này để F5 không đổi câu trả lời.

    private static AgentConversation Turn(string role, string message, int minute, bool verified = false) => new()
    {
        Role = role,
        Message = message,
        // Cột do SaveTurnAsync đóng dấu: model khai `ready` VÀ bản đồ bao phủ đã đủ. Đây là thứ cổng đọc —
        // KHÔNG phải chữ "Write Requirement" trong Message.
        ReadinessVerified = verified,
        CreatedAt = new DateTime(2026, 1, 1, 9, minute, 0, DateTimeKind.Utc)
    };

    // Một ca phủ cả ba trạng thái "ready" / "running" / "done": hàm không phân biệt chúng (chỉ "table"
    // là ngoại lệ). Ca thật: bấm "Write Requirement" xong ⇒ cổng đóng (running) ⇒ lời mời từng được cổng
    // nói thay bỗng hiện thành bong bóng ngay trên panel tiến độ — một lời mời bấm cái nút vừa bấm xong.
    [Fact]
    public void InviteTurnToHide_HidesTheInvite_WhenItIsTheLastTurnAndNoTableIsPending()
    {
        var invite = Turn("assistant", InviteMessage, 3, verified: true);
        var turns = new[] { Turn("user", "Đúng rồi", 1), Turn("assistant", "Mình ghi nhận.", 2), invite };

        Assert.Same(invite, RequirementReadinessGate.InviteTurnToHide(turns, tableGatePending: false));
    }

    // CÒN BẢNG CHỜ CHỐT (trạng thái "table"): cái đứng dưới lúc này là #tableGate, và nó tồn tại để giải
    // thích vì sao cái nút người dùng VỪA ĐƯỢC MỜI bấm lại không có. Giấu lời mời ở đó là lấy đi chính thứ
    // nó đang trả lời.
    [Fact]
    public void InviteTurnToHide_KeepsTheInvite_WhenATableIsPending()
    {
        var turns = new[] { Turn("assistant", InviteMessage, 1, verified: true) };

        Assert.Null(RequirementReadinessGate.InviteTurnToHide(turns, tableGatePending: true));
    }

    // Lời mời CŨ nằm giữa hội thoại không còn cổng nào đứng dưới để nói thay — giấu nó là đục một lỗ trong
    // lịch sử. Cùng luật với câu dẫn của các lượt hỏi gộp cũ: chúng vẫn giữ bong bóng.
    [Fact]
    public void InviteTurnToHide_KeepsAnOlderInvite_WhenTheConversationMovedOn()
    {
        var turns = new[]
        {
            Turn("assistant", InviteMessage, 1, verified: true),
            Turn("user", "Khoan, mình bổ sung thêm một ý", 2),
            Turn("assistant", "Vâng, anh/chị nói giúp mình.", 3)
        };

        Assert.Null(RequirementReadinessGate.InviteTurnToHide(turns, tableGatePending: false));
    }

    // Lượt cuối KHÔNG mang dấu mời thì là lượt thật (thường là một CÂU HỎI) — không có cổng nào đứng dưới
    // nói thay nó, nên giấu nó thì người dùng mất hẳn câu vừa được hỏi.
    [Fact]
    public void InviteTurnToHide_KeepsALastTurnThatIsNotAnInvite()
    {
        var turns = new[] { Turn("assistant", "Ai là người duyệt đơn khi trưởng phòng vắng?", 1) };

        Assert.Null(RequirementReadinessGate.InviteTurnToHide(turns, tableGatePending: false));
    }

    // NHẮC TÊN NÚT KHÔNG PHẢI LÀ MỜI — và đây đúng là ca mà phép dò chuỗi cũ đọc sai: lượt giải thích vì
    // sao CHƯA mở nút cũng chứa cụm "Write Requirement". Cột không mang dấu ⇒ bong bóng ở lại, cổng không
    // nói thay điều gì cả.
    [Fact]
    public void InviteTurnToHide_KeepsATurnThatOnlyMentionsTheButton()
    {
        var turns = new[]
        {
            Turn("assistant", "Mình chưa mở nút \"Write Requirement\" vì bảng báo cáo còn đang chờ anh/chị chốt.", 1)
        };

        Assert.Null(RequirementReadinessGate.InviteTurnToHide(turns, tableGatePending: false));
    }

    // Lượt cuối là của NGƯỜI DÙNG (F5 giữa lúc BA đang trả lời): không có bong bóng BA nào để giấu.
    [Fact]
    public void InviteTurnToHide_KeepsEverything_WhenTheLastTurnIsTheUser()
    {
        var turns = new[] { Turn("assistant", InviteMessage, 1, verified: true), Turn("user", "ok bạn", 2) };

        Assert.Null(RequirementReadinessGate.InviteTurnToHide(turns, tableGatePending: false));
    }

    // ---------- Lượt chat: lời mời đối chiếu tất định với bản đồ ----------

    [Fact]
    public async Task ChatAsync_InviteAndMapClear_KeepsInvite()
    {
        await SetCoverageMapAsync(BulletsAllClear);
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = InviteMessage, Ready = true } };

        await using var db = NewDb();
        var result = await NewChatSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép");

        var lastBaTurn = await LastAssistantTurnAsync();
        Assert.Equal(InviteMessage, lastBaTurn.Message);
        // Lời mời KHÔNG phải câu hỏi: không mở ô nhập thành "kể tự do", hành động lúc này là bấm nút.
        Assert.False(result.OpenEnded);
        // …và ĐÂY là thứ mở cổng: cờ trên lượt vừa lưu + cờ trên frame done. Không tầng nào đọc lại chữ
        // trong Message để suy ra điều này.
        Assert.True(lastBaTurn.ReadinessVerified);
        Assert.True(result.InvitesWriteRequirement);
    }

    // LƯỢT MỜI CÓ MESSAGE RỖNG — đúng hình dạng prompt dạy: bong bóng lời mời không bao giờ được vẽ (thẻ
    // cổng nói thay), nên model không viết tóm tắt/lời mời cho đỡ tốn token output. Rỗng ở đây KHÔNG phải
    // lượt câm: cổng vẫn mở, và lượt lưu xuống mang một câu mời cố định để transcript/lịch sử vẫn đọc đúng.
    [Fact]
    public async Task ChatAsync_ReadyWithEmptyMessage_OpensTheGateWithAFixedInvite()
    {
        await SetCoverageMapAsync(BulletsAllClear);
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = string.Empty, Ready = true } };

        await using var db = NewDb();
        var result = await NewChatSut(db, llm).ChatAsync(_projectId, "Nhân viên không hủy được, Supervisor reject");

        var lastBaTurn = await LastAssistantTurnAsync();
        Assert.True(lastBaTurn.ReadinessVerified);
        Assert.True(result.InvitesWriteRequirement);
        Assert.Equal(Loc.T("BAChatService.ApplyModelReply.2"), lastBaTurn.Message);
        Assert.False(result.OpenEnded);
    }

    // NHẮC TÊN NÚT KHÔNG PHẢI LÀ MỜI. Bản đồ đã đủ, nên nếu cổng còn đọc mặt chữ thì lượt này mở cổng —
    // trong khi model vừa nói ngược lại bằng chính cờ của nó. Ca thật mà phép dò chuỗi cũ đọc sai: lượt
    // giải thích vì sao CHƯA nên bấm nút cũng chứa cụm "Write Requirement".
    [Fact]
    public async Task ChatAsync_MentionsTheButtonWithoutReadyFlag_KeepsTheGateClosed()
    {
        await SetCoverageMapAsync(BulletsAllClear);
        var llm = new FakeLlm
        {
            ChatReply = new BAChatReply
            {
                Message = "Mình chưa bật nút \"Write Requirement\" vì còn một điểm muốn chốt: "
                          + "khi trưởng phòng vắng thì ai duyệt đơn ạ?",
                Suggestions = new List<string> { "Phó phòng", "Giám đốc" },
                Ready = false
            }
        };

        await using var db = NewDb();
        var result = await NewChatSut(db, llm).ChatAsync(_projectId, "Vậy là xong rồi đúng không?");

        var lastBaTurn = await LastAssistantTurnAsync();
        Assert.False(lastBaTurn.ReadinessVerified);
        Assert.False(result.InvitesWriteRequirement);
        // Câu hỏi thật của lượt vẫn còn nguyên trên màn hình.
        Assert.Contains("ai duyệt đơn", lastBaTurn.Message, StringComparison.Ordinal);
    }

    // CỜ NÓI ĐỦ, NỘI DUNG ĐANG HỎI ⇒ TIN NỘI DUNG. Phép dò chuỗi cũ vô tình đỡ được ca này (không có cụm
    // nào trong câu hỏi ⇒ cổng đóng); cổng đi theo cờ thì phải tự dựng lại chốt chặn đó, nếu không câu hỏi
    // vừa lên màn hình đã bị cổng nói thay và người dùng mất trắng nó.
    [Fact]
    public async Task ChatAsync_ReadyFlagOnATurnThatIsStillAsking_IsDropped()
    {
        await SetCoverageMapAsync(BulletsAllClear);
        var llm = new FakeLlm
        {
            ChatReply = new BAChatReply
            {
                Message = "Trước khi mình soạn tài liệu, khi trưởng phòng vắng thì ai duyệt đơn ạ?",
                Suggestions = new List<string> { "Phó phòng", "Giám đốc" },
                Ready = true
            }
        };

        await using var db = NewDb();
        var result = await NewChatSut(db, llm).ChatAsync(_projectId, "Ừ, gần xong rồi");

        var lastBaTurn = await LastAssistantTurnAsync();
        Assert.False(lastBaTurn.ReadinessVerified);
        Assert.False(result.InvitesWriteRequirement);
        Assert.Contains("ai duyệt đơn", lastBaTurn.Message, StringComparison.Ordinal);
        // Chip của câu hỏi cũng còn: lượt này vẫn là một lượt hỏi bình thường.
        Assert.Equal(new[] { "Phó phòng", "Giám đốc" }, result.Suggestions);
    }

    [Fact]
    public async Task ChatAsync_InviteButMapMissing_ReplacesInviteWithDeterministicQuestion()
    {
        await SetCoverageMapAsync(BulletsMissingRules);
        var llm = new FakeLlm
        {
            ChatReply = new BAChatReply { Message = InviteMessage, Ready = true }
        };

        await using var db = NewDb();
        var result = await NewChatSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép");

        var lastBaTurn = await LastAssistantTurnAsync();
        // Lời mời bị thay bằng câu hỏi hỏi đúng mẩu còn thiếu ⇒ nút "Write Requirement" giữ trạng thái mờ
        // (UI nhận diện lời mời qua chuỗi "Write Requirement" trong lượt BA mới nhất) — panel 1 nhóm
        // thiếu và nút mờ giờ kể CÙNG một câu chuyện vì đọc cùng bản đồ.
        Assert.Contains("hạn mức ngày phép", lastBaTurn.Message);
        Assert.DoesNotContain("Write Requirement", lastBaTurn.Message, StringComparison.OrdinalIgnoreCase);
        // Câu của cổng không kèm chip ⇒ phải là câu MỞ, để khung chat mời người dùng gõ vào ô nhập thay
        // vì bày ra một câu hỏi không có chỗ trả lời.
        Assert.True(result.OpenEnded);
        Assert.Null(lastBaTurn.Suggestions);
    }

    [Fact]
    public async Task ChatAsync_InviteButNoMapYet_FailsClosed_ReplacesInvite()
    {
        // Bản đồ chưa từng gộp được (vd distill lỗi từ đầu) ⇒ fail-closed: không giữ lời mời.
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = InviteMessage, Ready = true } };

        await using var db = NewDb();
        await NewChatSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép");

        Assert.DoesNotContain("Write Requirement", (await LastAssistantTurnAsync()).Message, StringComparison.OrdinalIgnoreCase);
    }

    // Lượt chat là NƠI DUY NHẤT tự dựng ra dấu verify: cổng tất định vừa xét trên bản đồ hiện hành ngay
    // trước khi lời mời được lưu, nên dấu đó là kết luận của cổng chứ không phải suy đoán của tầng sau.
    [Fact]
    public async Task ChatAsync_InviteAndMapClear_StampsReadinessVerified()
    {
        await SetCoverageMapAsync(BulletsAllClear);
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = InviteMessage, Ready = true } };

        await using var db = NewDb();
        await NewChatSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép");

        Assert.True((await LastAssistantTurnAsync()).ReadinessVerified);
    }

    // Bản đồ còn thiếu ⇒ lời mời bị thay bằng câu chặn, và lượt đó TUYỆT ĐỐI không được mang dấu: nếu
    // không, bước soạn tài liệu sẽ bỏ qua đúng cái cổng vừa chặn.
    [Fact]
    public async Task ChatAsync_InviteButMapIncomplete_DoesNotStampReadinessVerified()
    {
        await SetCoverageMapAsync(BulletsMissingRules);
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = InviteMessage, Ready = true } };

        await using var db = NewDb();
        await NewChatSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép");

        var stored = await LastAssistantTurnAsync();
        Assert.False(stored.ReadinessVerified);
        Assert.DoesNotContain("Write Requirement", stored.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Bước sinh tài liệu ----------

    [Fact]
    public async Task GenerateOrUpdateDraft_LastTurnCarriesVerifiedFlag_SkipsGate_AndDrafts()
    {
        await SeedTurnsAsync(verifiedLastTurn: true, ("user", "Tôi muốn app quản lý đơn nghỉ phép"), ("assistant", InviteMessage));
        var llm = new FakeLlm
        {
            // Van "không giả định" của bước soạn: dừng trước khi ghi file để test không đụng file hệ thống,
            // nhưng vẫn chứng minh đã đi THẲNG vào soạn tài liệu (ProductBriefCalls) mà không xét lại bản đồ.
            ProductBrief = new BAProductBriefResult { NeedsClarification = true, ClarifyingQuestion = "Còn thiếu một điểm?" }
        };

        await using var db = NewDb();
        var outcome = await NewDraftSut(db, llm).GenerateOrUpdateDraftAsync(_projectId);

        Assert.Equal(1, llm.ProductBriefCalls);
        // Nhánh lời-mời-đã-duyệt không cần gộp bản đồ (không có gì mới kể từ lời mời).
        Assert.Equal(0, llm.CoverageCalls);
        Assert.Equal(RequirementDraftOutcome.NeedsMoreInfo, outcome);
    }

    // Đường tắt khoá bằng CỜ, không bằng chữ trong lượt cuối. Một lượt mang đúng cụm "Write Requirement"
    // nhưng không có dấu verify (lượt cũ ghi trước khi có cột, hoặc một đường ghi khác chép lại lời mời)
    // KHÔNG được mở đường tắt: cổng xét lại như thường — fail-closed, cùng luật với mọi chốt chặn khác ở
    // tuyến này.
    [Fact]
    public async Task GenerateOrUpdateDraft_InviteTextWithoutFlag_StillReEvaluatesGate()
    {
        await SetCoverageMapAsync(BulletsMissingRules);
        await SeedTurnsAsync(("user", "Tôi muốn app quản lý đơn nghỉ phép"), ("assistant", InviteMessage));
        var llm = new FakeLlm();

        await using var db = NewDb();
        var outcome = await NewDraftSut(db, llm).GenerateOrUpdateDraftAsync(_projectId);

        Assert.Equal(0, llm.ProductBriefCalls);
        Assert.Equal(RequirementDraftOutcome.NeedsMoreInfo, outcome);
    }

    [Fact]
    public async Task GenerateOrUpdateDraft_LastTurnIsUserMessage_MapMissing_BlocksWithDeterministicQuestion()
    {
        await SetCoverageMapAsync(BulletsMissingRules);
        await SeedTurnsAsync(("user", "Tôi muốn app quản lý đơn nghỉ phép"), ("assistant", InviteMessage), ("user", "à thêm phần báo cáo nữa"));
        var llm = new FakeLlm();

        await using var db = NewDb();
        var outcome = await NewDraftSut(db, llm).GenerateOrUpdateDraftAsync(_projectId);

        // Có lượt user mới sau lời mời ⇒ gộp nốt vào bản đồ rồi xét tất định. Distill lỗi (FakeLlm) ⇒
        // thử lại một lần rồi giữ bản đồ cũ — còn nhóm thiếu ⇒ chặn, KHÔNG soạn tài liệu (fail-closed).
        Assert.Equal(2, llm.CoverageCalls);
        Assert.Equal(0, llm.ProductBriefCalls);
        Assert.Equal(RequirementDraftOutcome.NeedsMoreInfo, outcome);
        Assert.Contains("hạn mức ngày phép", (await LastAssistantTurnAsync()).Message);
    }

    [Fact]
    public async Task GenerateOrUpdateDraft_LastTurnIsUserMessage_MapClear_Drafts()
    {
        await SetCoverageMapAsync(BulletsAllClear);
        await SeedTurnsAsync(("user", "Tôi muốn app quản lý đơn nghỉ phép"), ("assistant", InviteMessage), ("user", "ok cứ thế nhé"));
        var llm = new FakeLlm
        {
            ProductBrief = new BAProductBriefResult { NeedsClarification = true, ClarifyingQuestion = "Còn thiếu một điểm?" }
        };

        await using var db = NewDb();
        var outcome = await NewDraftSut(db, llm).GenerateOrUpdateDraftAsync(_projectId);

        // Distill lỗi (FakeLlm) ⇒ thử lại một lần; bản đồ cũ đã đủ nên cổng vẫn mở và tài liệu vẫn được soạn.
        Assert.Equal(2, llm.CoverageCalls);
        Assert.Equal(1, llm.ProductBriefCalls);
        Assert.Equal(RequirementDraftOutcome.NeedsMoreInfo, outcome);
    }

    // Ghi CẢ HAI cột: cổng đọc bản đồ để biết đủ chưa và đọc danh sách câu hỏi để biết hỏi gì, nên một
    // fixture chỉ ghi một cột là dựng ra một trạng thái không bao giờ tồn tại trong production.
    private async Task SetCoverageMapAsync(string bullets)
    {
        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        project.RequirementCoverageMap = CoverageMapFixture.Map(bullets);
        project.OpenQuestions = CoverageMapFixture.StoredQuestions(bullets);
        await db.SaveChangesAsync();
    }

    private async Task<AgentConversation> LastAssistantTurnAsync()
    {
        await using var db = NewDb();
        return await db.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "assistant")
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .LastAsync();
    }

    private Task SeedTurnsAsync(params (string Role, string Message)[] turns)
        => SeedTurnsAsync(verifiedLastTurn: false, turns);

    // verifiedLastTurn: đóng dấu AgentConversation.ReadinessVerified lên lượt CUỐI — đúng thứ mà lượt chat
    // đặt khi cổng tất định cho lời mời đi qua. Nội dung lượt KHÔNG còn là tín hiệu, nên một lời mời không
    // có dấu phải được đối xử như mọi lượt khác (xem test bên dưới).
    private async Task SeedTurnsAsync(bool verifiedLastTurn, params (string Role, string Message)[] turns)
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < turns.Length; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = turns[i].Role,
                Message = turns[i].Message,
                ReadinessVerified = verifiedLastTurn && i == turns.Length - 1,
                CreatedAt = baseTime.AddSeconds(i)
            });
        }
        await db.SaveChangesAsync();
    }

    private static BAChatService NewChatSut(AppDbContext db, ILlmClient llm)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new StubPrompts();
        return new BAChatService(
            db,
            llm,
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            new BAChatReplyParser(),
            new ConversationMemoryService(db, llm, prompts),
            new UserMemoryService(db, llm, prompts),
            new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts)),
            NewOrgContext(db, prompts),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new ReportMapDraftService(db, llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)));
    }

    private static ProductBriefDraftService NewDraftSut(AppDbContext db, ILlmClient llm)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new StubPrompts();
        var catalog = new ProjectArtifactCatalog();
        var templateService = new RequirementTemplateService(new FakeWebHostEnvironment());
        return new ProductBriefDraftService(
            db,
            llm,
            new RequirementPromptBuilder(),
            new RequirementResponseParser(),
            new RequirementDocumentGenerator(db, templateService, new DocxTemplateWriter(), catalog, new FakeArtifactStorage()),
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            catalog,
            new ProductBriefReviewParser(),
            NewOrgContext(db, prompts),
            new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts)),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new ConversationMemoryService(db, llm, prompts),
            DesignCallFixture.NewService(db, llm, prompts));
    }

    // OrgUnits trống trong các test này ⇒ service trả null (fail-open), không thêm system message nào.
    private static OrganizationContextService NewOrgContext(AppDbContext db, PromptTemplateService prompts) =>
        new(db, prompts, new OrgChartProvider(db, new MemoryCache(new MemoryCacheOptions())),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Fake ILlmClient trả kết quả structured theo Purpose: lượt chat (BAChat) và lượt soạn Product Brief
    // (BAProductBrief) — KHÔNG còn purpose readiness nào (cổng giờ tất định). ChatWithLogAsync (gộp bản
    // đồ bao phủ + các bộ nhớ phụ) trả lỗi để các service đó fail-open giữ nguyên bản đồ test đã seed;
    // đếm riêng lời gọi distill bản đồ (BARequirementCoverage) để chốt nhánh nào phải/không phải gộp thêm.
    private sealed class FakeLlm : ILlmClient
    {
        public BAChatReply ChatReply = new() { Message = "Đã ghi nhận." };
        public BAProductBriefResult? ProductBrief;
        public int ProductBriefCalls;
        public int CoverageCalls;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            if (logContext.Purpose == "BARequirementCoverage")
                CoverageCalls++;
            return Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "fail-open path in tests" });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {

            // Bản đồ bao phủ nay đi qua đường structured output (RequirementCoverageService). Fake trả về
            // Value null để service rơi xuống nhánh parse văn xuôi — đúng nhánh mà một model không nhận
            // response_format sẽ chạy — nên các test ở đây vẫn seed bản đồ bằng text như trước.
            if (logContext.Purpose == "BARequirementCoverage")
                return Task.FromResult((ChatWithLogAsync(model, messages, logContext, onToken, cancellationToken).Result, (T?)null));
            // Phần thiết kế chạy trước khi soạn Brief (DesignPreparationService) — trả bản tối thiểu.
            if (DesignCallFixture.TryAnswer(logContext.Purpose, out var design))
                return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)design));
            object? value = logContext.Purpose switch
            {
                "BAChat" => ChatReply,
                "BAProductBrief" => ProductBrief,
                _ => throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}")
            };

            if (logContext.Purpose == "BAProductBrief")
                ProductBriefCalls++;

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)value));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }

    private sealed class FakeArtifactStorage : IArtifactStorage
    {
        public void InitializeProjectWorkspace(string projectKey) { }
        public bool TryRenameProjectWorkspace(string oldProjectKey, string newProjectKey) => true;
        public bool TryCopyProjectWorkspace(string sourceProjectKey, string targetProjectKey, IReadOnlyCollection<string>? onlyTopLevelFolders = null) => true;
        public void TryDeleteProjectWorkspace(string projectKey) { }
        public string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact) => Path.Combine(Path.GetTempPath(), artifact.FileName);
        public string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact) => Path.Combine(Path.GetTempPath(), versionName, artifact.FileName);
        public string GetSourceUploadDir(string projectKey) => Path.GetTempPath();
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }
}
