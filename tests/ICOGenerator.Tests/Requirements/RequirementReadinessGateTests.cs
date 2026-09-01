using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Requirements.Templates;
using ICOGenerator.Services.Security;
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

    private static readonly string MapAllClear = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép.
        - ★ Đối tượng người dùng & vai trò: [RÕ] Nhân viên nộp, quản lý duyệt.
        - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] người dùng không cần.
        """);

    private static readonly string MapMissingRules = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép.
        - Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Trừ quỹ phép năm; còn thiếu: hạn mức ngày phép.
        """);

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
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app nghỉ phép" });
        db.SaveChanges();
    }

    // ---------- Evaluate thuần (không DB, không LLM) ----------

    [Fact]
    public void Evaluate_AllApplicableClear_IsReady()
    {
        var readiness = RequirementReadinessGate.Evaluate(MapAllClear);

        Assert.True(readiness.Ready);
    }

    [Fact]
    public void Evaluate_PartialLine_NotReady_QuestionAsksTheGapWithoutNamingTheGroup()
    {
        var readiness = RequirementReadinessGate.Evaluate(MapMissingRules);

        Assert.False(readiness.Ready);
        // Câu hỏi dựng sẵn phải hỏi ĐÚNG nội dung phần "còn thiếu" distiller đã ghi — thành câu, không bê
        // nguyên cụm bookkeeping "còn thiếu:" ra cho người dùng đọc, và KHÔNG đọc nhãn nhóm của bản đồ ra
        // màn hình. Không được chứa "Write Requirement" (chuỗi đó là tín hiệu làm nổi nút trên UI).
        Assert.Contains("hạn mức ngày phép", readiness.Message);
        Assert.DoesNotContain("Quy tắc nghiệp vụ & ràng buộc", readiness.Message);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
        Assert.DoesNotContain("Write Requirement", readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    // MẨU "còn thiếu" RỖNG NGHĨA không được lên màn hình. Cổng phát nguyên văn mẩu đó, nên một mẩu chỉ
    // nói rằng "vẫn còn gì đó" là một lượt mất trắng — và tệ hơn: một tiếng "không có" đủ để lật dòng lên
    // [RÕ] và mở cổng bằng một câu hỏi rỗng.
    //
    // Ca thật (dự án JD Libary 5, lượt 26 — lượt CUỐI của buổi phỏng vấn): người dùng nhận đúng
    // "Anh/chị cho mình hỏi thêm: các quy tắc khác (nếu có) — anh/chị cho mình xin thông tin này nhé?".
    [Theory]
    [InlineData("các quy tắc khác (nếu có)")]
    [InlineData("thông tin bổ sung")]
    [InlineData("các điểm còn lại")]
    [InlineData("chi tiết khác")]
    public void Evaluate_HollowGap_FallsBackToReplayingWhatWasRecorded(string hollowGap)
    {
        var map = CoverageMapFixture.Map("- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] JD phải qua HRBP verify rồi HoD approve mới "
                  + $"available để assign. còn thiếu: {hollowGap}");

        var readiness = RequirementReadinessGate.Evaluate(map);

        Assert.False(readiness.Ready);
        // Không phát mẩu rỗng…
        Assert.DoesNotContain(hollowGap, readiness.Message, StringComparison.OrdinalIgnoreCase);
        // …mà rơi về nhánh PHÁT LẠI: chở theo điều đã ghi nhận nên người dùng đọc là trả lời được, và nó
        // là một câu ĐÓNG LẠI ĐƯỢC bằng một lượt.
        Assert.Contains("HRBP verify", readiness.Message, StringComparison.Ordinal);
        Assert.EndsWith("?", readiness.Message.Trim(), StringComparison.Ordinal);
    }

    // Ranh giới: mẩu chở nội dung nghiệp vụ thật vẫn được phát nguyên văn, kể cả khi nó cũng kết bằng chữ
    // "khác" — phép thử bắt HÌNH DẠNG (đầu mê-ta + đuôi "khác"), không bắt mặt chữ.
    [Fact]
    public void Evaluate_AGapCarryingRealContent_IsStillAsked()
    {
        var map = CoverageMapFixture.Map("- Vòng đời & trạng thái: [MỘT PHẦN] JD có trạng thái submit, verify, approve. "
                  + "còn thiếu: tên chính thức của các trạng thái và điều kiện chuyển giữa chúng");

        var readiness = RequirementReadinessGate.Evaluate(map);

        Assert.Contains("tên chính thức của các trạng thái", readiness.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CoreGroupAskedBeforeSecondary()
    {
        var map = CoverageMapFixture.Map("""
            - Quy mô sử dụng: [CHƯA HỎI]
            - ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] còn thiếu: luồng duyệt.
            """);

        var readiness = RequirementReadinessGate.Evaluate(map);

        Assert.False(readiness.Ready);
        // Dòng ★ cốt lõi được hỏi trước dù đứng sau trong bản đồ — và chỉ hỏi MỘT chỗ, dòng phụ để dành
        // lượt sau chứ không bị hỏi dồn trong cùng lượt.
        Assert.Contains("luồng duyệt", readiness.Message);
        Assert.DoesNotContain(CoverageGroupOpeners.Find("Quy mô sử dụng")!, readiness.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chưa có bản đồ nào cả")] // không parse được dòng nào — như chưa có bản đồ.
    public void Evaluate_MissingMap_FailsClosed(string? map)
    {
        var readiness = RequirementReadinessGate.Evaluate(map);

        Assert.False(readiness.Ready);
        Assert.False(string.IsNullOrWhiteSpace(readiness.Message));
    }

    [Fact]
    public void Evaluate_AllNotApplicable_IsBrokenMap_NotReady()
    {
        var readiness = RequirementReadinessGate.Evaluate(CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [KHÔNG ÁP DỤNG] ?"));

        Assert.False(readiness.Ready);
    }

    // ---------- Lượt chat: lời mời đối chiếu tất định với bản đồ ----------

    [Fact]
    public async Task ChatAsync_InviteAndMapClear_KeepsInvite()
    {
        await SetCoverageMapAsync(MapAllClear);
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = InviteMessage, Ready = true } };

        await using var db = NewDb();
        var result = await NewChatSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép");

        Assert.Equal(InviteMessage, (await LastAssistantTurnAsync()).Message);
        // Lời mời KHÔNG phải câu hỏi: không mở ô nhập thành "kể tự do", hành động lúc này là bấm nút.
        Assert.False(result.OpenEnded);
    }

    [Fact]
    public async Task ChatAsync_InviteButMapMissing_ReplacesInviteWithDeterministicQuestion()
    {
        await SetCoverageMapAsync(MapMissingRules);
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

    // Lượt mời KHÔNG còn vẽ sơ đồ luồng: luồng đã được người dùng tự tay duyệt ở BẢNG LUỒNG từ giữa buổi
    // (không bảng nào khác được bày trước khi bảng luồng chốt — xem InterviewTableGate), nên một bản vẽ
    // chỉ-đọc ngay trước nút chỉ nói lại điều đã chốt mà không sửa được. Cột FlowDiagram vẫn còn cho dữ
    // liệu CŨ, nhưng lượt mới không được ghi vào nó nữa.
    [Fact]
    public async Task ChatAsync_InviteAndMapClear_StoresNoFlowDiagram()
    {
        await SetCoverageMapAsync(MapAllClear);
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = InviteMessage, Ready = true } };

        await using var db = NewDb();
        await NewChatSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép");

        var stored = await LastAssistantTurnAsync();
        Assert.Equal(InviteMessage, stored.Message);
        Assert.Null(stored.FlowDiagram);
    }

    // Lượt chat là NƠI DUY NHẤT tự dựng ra dấu verify: cổng tất định vừa xét trên bản đồ hiện hành ngay
    // trước khi lời mời được lưu, nên dấu đó là kết luận của cổng chứ không phải suy đoán của tầng sau.
    [Fact]
    public async Task ChatAsync_InviteAndMapClear_StampsReadinessVerified()
    {
        await SetCoverageMapAsync(MapAllClear);
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
        await SetCoverageMapAsync(MapMissingRules);
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
        await SetCoverageMapAsync(MapMissingRules);
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
        await SetCoverageMapAsync(MapMissingRules);
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
        await SetCoverageMapAsync(MapAllClear);
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

    private async Task SetCoverageMapAsync(string map)
    {
        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        project.RequirementCoverageMap = map;
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
            new RequirementCoverageService(db, llm, prompts),
            NewOrgContext(db, prompts),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new InterviewOutlookService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
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
            new RequirementDocumentGenerator(db, templateService, new DocxTemplateWriter(), new WorkspacePathResolver(config), catalog, new FakeArtifactStorage()),
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            catalog,
            new ChecklistGapMemoryService(db, llm, prompts, new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)), NullLogger<ChecklistGapMemoryService>.Instance),
            new ProductBriefReviewParser(),
            NewOrgContext(db, prompts),
            new RequirementCoverageService(db, llm, prompts),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new ConversationMemoryService(db, llm, prompts));
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

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            if (logContext.Purpose == "BARequirementCoverage")
                CoverageCalls++;
            return Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "fail-open path in tests" });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {

            // Bản đồ bao phủ nay đi qua đường structured output (RequirementCoverageService). Fake trả về
            // Value null để service rơi xuống nhánh parse văn xuôi — đúng nhánh mà một model không nhận
            // response_format sẽ chạy — nên các test ở đây vẫn seed bản đồ bằng text như trước.
            if (logContext.Purpose == "BARequirementCoverage")
                return Task.FromResult((ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).Result, (T?)null));
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

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
