using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng "chốt nhanh phần còn lại": đổi N lượt hỏi lẻ thành MỘT lượt duyệt. Năm bất biến phải giữ:
//  1. Chỉ đề xuất cho các nhóm ĐANG TRỐNG của bản đồ — model trả thêm chủ đề khác thì bị loại.
//  2. Điều người dùng chốt được ghi vào HỘI THOẠI (nguồn sự thật của mọi tầng chắt lọc), không phải một
//     cột riêng — nếu không, lượt distill kế tiếp sẽ viết đè.
//  3. Bản đồ được gộp NGAY trong lượt này, để thanh tiến độ/nút "Write Requirement" phản hồi tức thì.
//  4. Lượt BA đóng lượt chỉ được MỜI bấm "Write Requirement" khi cổng tất định pass — cùng bất biến với
//     đường chat, nếu không nút và panel sẽ vênh nhau.
//  5. "Suy ra" phải KIẾM ĐƯỢC bằng một trích dẫn thật, không phải bằng lời tự khai của model: UI chỉ
//     chọn sẵn phần suy ra, nên một phương án bịa đội lốt "suy ra" sẽ đi thẳng vào hội thoại như lời của
//     chính người dùng. Đây là lỗ hổng đã làm cổng chậm hơn cả trả lời từng câu trong chat.
public class QuickCloseGapsTests : IDisposable
{
    private const string PartialMap = """
        - ★ Mục tiêu / bài toán: [RÕ] quản lý đơn nghỉ phép
        - ★ Đối tượng người dùng & vai trò: [RÕ] nhân viên, quản lý
        - Thông báo / nhắc nhở: [CHƯA HỎI]
        - Báo cáo / thống kê: [MỘT PHẦN] còn thiếu: ai được xem
        """;

    private const string FullMap = """
        - ★ Mục tiêu / bài toán: [RÕ] quản lý đơn nghỉ phép
        - ★ Đối tượng người dùng & vai trò: [RÕ] nhân viên, quản lý
        - Thông báo / nhắc nhở: [RÕ] quản lý nhận thông báo
        - Báo cáo / thống kê: [RÕ] trưởng phòng xem báo cáo tháng
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public QuickCloseGapsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        db.AiModels.Add(model);
        var ba = new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id };
        db.Agents.Add(ba);
        db.Projects.Add(new Project { Id = _projectId, Name = "P", RequirementCoverageMap = PartialMap });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = ba.Id,
            Role = "user",
            Message = "Tôi muốn làm app quản lý đơn nghỉ phép."
        });
        db.SaveChanges();
    }

    // ---- Bước 1: soạn phương án ----

    [Fact]
    public async Task Propose_ReturnsOneProposalPerPendingGroup_AndDropsInventedTopics()
    {
        await using var db = NewDb();
        var llm = new FakeLlm
        {
            Proposals = new GapProposalSet
            {
                Proposals =
                {
                    new GapProposal { Group = "Thông báo / nhắc nhở", Question = "Ai được báo?", Proposal = "Quản lý nhận thông báo khi có đơn mới." },
                    new GapProposal { Group = "Báo cáo / thống kê", Question = "Báo cáo nào?", Proposal = "Trưởng phòng xem báo cáo ngày phép còn lại theo tháng." },
                    // Nhóm ĐÃ [RÕ] — không được lấp lại (sẽ ghi đè điều người dùng đã nói).
                    new GapProposal { Group = "Mục tiêu / bài toán", Question = "Làm gì?", Proposal = "Quản lý kho vật tư." }
                }
            }
        };

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        Assert.Equal(ProposeGapsStatus.Ok, outcome.Status);
        Assert.Equal(2, outcome.Proposals.Count);
        Assert.Equal("Thông báo / nhắc nhở", outcome.Proposals[0].Group);
        Assert.Equal("Báo cáo / thống kê", outcome.Proposals[1].Group);
    }

    // gpt-5-nano chép nguyên dòng của prompt vào `group` ("Thông báo / nhắc nhở: [CHƯA HỎI]") trong khi
    // deepseek-v4-flash trả nhãn trần. Khớp nhãn thô ⇒ CẢ BỘ phương án bị loại và cổng báo "chưa soạn
    // được phương án" dù lời gọi thành công — cổng phải chịu được cả hai kiểu viết nhãn.
    [Fact]
    public async Task Propose_WhenTheModelCopiesTheWholeMapLineAsTheGroupLabel_StillAligns()
    {
        await using var db = NewDb();
        var llm = new FakeLlm
        {
            Proposals = new GapProposalSet
            {
                Proposals =
                {
                    new GapProposal { Group = "Thông báo / nhắc nhở: [CHƯA HỎI]", Question = "Ai được báo?", Proposal = "Quản lý nhận thông báo khi có đơn mới." },
                    new GapProposal { Group = "- ★ Báo cáo / thống kê: [MỘT PHẦN] còn thiếu: ai được xem", Question = "Báo cáo nào?", Proposal = "Trưởng phòng xem báo cáo tháng." }
                }
            }
        };

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        Assert.Equal(ProposeGapsStatus.Ok, outcome.Status);
        Assert.Equal(2, outcome.Proposals.Count);
        Assert.Equal("Thông báo / nhắc nhở", outcome.Proposals[0].Group);
        Assert.Equal("Báo cáo / thống kê", outcome.Proposals[1].Group);
    }

    // Nhãn viết dài/ngắn hơn bản đồ vẫn ghép được, nhưng mỗi mục model chỉ được dùng cho MỘT nhóm —
    // nếu không, một nhãn chung chung sẽ bị nhân bản thành phương án cho nhiều nhóm khác nhau.
    [Fact]
    public async Task Propose_MatchesLooselyWordedLabels_ButNeverReusesOneProposalForTwoGroups()
    {
        await using var db = NewDb();
        var llm = new FakeLlm
        {
            Proposals = new GapProposalSet
            {
                Proposals =
                {
                    new GapProposal { Group = "**Thông báo / nhắc nhở của hệ thống**", Question = "Ai được báo?", Proposal = "Quản lý nhận thông báo khi có đơn mới." }
                }
            }
        };

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        var only = Assert.Single(outcome.Proposals);
        Assert.Equal("Thông báo / nhắc nhở", only.Group);
        Assert.Equal("Quản lý nhận thông báo khi có đơn mới.", only.Proposal);
    }

    [Fact]
    public async Task Propose_WhenNothingIsPending_SaysSo_WithoutCallingTheModel()
    {
        await using var seed = NewDb();
        (await seed.Projects.SingleAsync()).RequirementCoverageMap = FullMap;
        await seed.SaveChangesAsync();

        await using var db = NewDb();
        var llm = new FakeLlm();

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        Assert.Equal(ProposeGapsStatus.NothingPending, outcome.Status);
        Assert.Equal(0, llm.StructuredCalls);
    }

    // Lời gọi soạn phương án lỗi KHÔNG được làm hỏng gì: người dùng vẫn còn đường trả lời trong chat.
    [Fact]
    public async Task Propose_WhenTheModelFails_FailsOpenWithoutTouchingTheConversation()
    {
        await using var db = NewDb();

        var outcome = await NewProposeSut(db, new FakeLlm { Fail = true }).ExecuteAsync(_projectId);

        Assert.Equal(ProposeGapsStatus.ProposalFailed, outcome.Status);
        Assert.Equal(1, await NewDb().AgentConversations.CountAsync());
    }

    // Structured output là TÙY CHỌN của từng model và mặc định TẮT (nhiều server OpenAI-compatible tự
    // host từ chối response_format), nên đường đi THẬT của app là "value null + JSON nằm trong text".
    // Không parse được nhánh đó thì cổng này không bao giờ chạy trên chính cấu hình mặc định.
    [Fact]
    public async Task Propose_WhenTheModelHasNoStructuredOutput_StillParsesTheJsonFromPlainText()
    {
        await using var db = NewDb();
        var llm = new FakeLlm
        {
            Structured = null,
            RawContent = """
                Đây là các phương án:
                ```json
                { "proposals": [ { "group": "Thông báo / nhắc nhở", "question": "Ai được báo?", "proposal": "Quản lý nhận thông báo khi có đơn mới." } ] }
                ```
                """
        };

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        Assert.Equal(ProposeGapsStatus.Ok, outcome.Status);
        var only = Assert.Single(outcome.Proposals);
        Assert.Equal("Thông báo / nhắc nhở", only.Group);
    }

    // ---- Bước 1b: "suy ra" hay "phỏng đoán" ----

    [Fact]
    public async Task Propose_MarksAProposalAsGrounded_WhenTheModelQuotesWhatTheUserActuallySaid()
    {
        await using var db = NewDb();
        var llm = new FakeLlm
        {
            Proposals = new GapProposalSet
            {
                Proposals =
                {
                    new GapProposal
                    {
                        Group = "Thông báo / nhắc nhở",
                        Question = "Ai được báo?",
                        Confidence = "suy-ra",
                        Basis = "app quản lý đơn nghỉ phép, có quản lý duyệt",
                        Proposal = "Quản lý nhận thông báo khi có đơn mới."
                    }
                }
            }
        };

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        var only = Assert.Single(outcome.Proposals);
        Assert.True(GapProposal.IsGrounded(only));
        Assert.Equal("app quản lý đơn nghỉ phép, có quản lý duyệt", only.Basis);
    }

    // Model yếu bị ép điền một trường bắt buộc sẽ khai "suy-ra" rồi bỏ trống căn cứ. Tin lời khai đó là
    // quay lại đúng hành vi cũ: cả danh sách phỏng đoán được tick sẵn và đi thẳng vào hội thoại.
    [Theory]
    [InlineData("suy-ra", "")]
    [InlineData("suy-ra", "hội thoại")]
    [InlineData("", "app quản lý đơn nghỉ phép")]
    public async Task Propose_FallsBackToGuess_WhenTheClaimIsNotBackedByARealQuote(string confidence, string basis)
    {
        await using var db = NewDb();
        var llm = new FakeLlm
        {
            Proposals = new GapProposalSet
            {
                Proposals =
                {
                    new GapProposal
                    {
                        Group = "Thông báo / nhắc nhở",
                        Confidence = confidence,
                        Basis = basis,
                        Proposal = "Quản lý nhận thông báo khi có đơn mới."
                    }
                }
            }
        };

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        var only = Assert.Single(outcome.Proposals);
        Assert.False(GapProposal.IsGrounded(only));
        // Căn cứ không đạt thì phải BIẾN MẤT, không được hiện dưới một dòng đang gắn nhãn phỏng đoán.
        Assert.Equal(string.Empty, only.Basis);
    }

    // Chép lại chính phương án vào ô căn cứ là cách "vòng tròn" mà model hay dùng để qua mặt yêu cầu
    // trích dẫn — nó không chứng minh được gì về điều người dùng đã nói.
    [Fact]
    public async Task Propose_FallsBackToGuess_WhenTheBasisJustRepeatsTheProposal()
    {
        await using var db = NewDb();
        var llm = new FakeLlm
        {
            Proposals = new GapProposalSet
            {
                Proposals =
                {
                    new GapProposal
                    {
                        Group = "Thông báo / nhắc nhở",
                        Confidence = "suy-ra",
                        Basis = "Quản lý nhận thông báo khi có đơn mới.",
                        Proposal = "Quản lý  nhận thông báo khi có đơn mới."
                    }
                }
            }
        };

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        Assert.False(GapProposal.IsGrounded(Assert.Single(outcome.Proposals)));
    }

    // Ca nguy hiểm nhất, và là lý do phải đối chiếu chứ không chỉ đếm chữ: model TỰ BỊA một câu trích
    // nghe rất giống lời người dùng. Người dùng không hề nói gì về ca làm việc — dòng này phải rơi về
    // phỏng đoán, nếu không nó được chọn sẵn và đi thẳng vào hội thoại như lời của chính họ.
    [Fact]
    public async Task Propose_FallsBackToGuess_WhenTheQuoteAppearsNowhereInTheConversation()
    {
        await using var db = NewDb();
        var llm = new FakeLlm
        {
            Proposals = new GapProposalSet
            {
                Proposals =
                {
                    new GapProposal
                    {
                        Group = "Thông báo / nhắc nhở",
                        Confidence = "suy-ra",
                        Basis = "anh/chị nói bộ phận vận hành chạy ba ca luân phiên mỗi ngày",
                        Proposal = "Trưởng ca nhận thông báo khi có đơn mới."
                    }
                }
            }
        };

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        Assert.False(GapProposal.IsGrounded(Assert.Single(outcome.Proposals)));
    }

    // Chip lựa chọn chỉ có giá trị khi bấm vào là ĐỔI được câu trả lời: bản trùng phương án/trùng nhau
    // tốn một dòng đọc mà không cho thêm lựa chọn nào.
    [Fact]
    public async Task Propose_KeepsOnlyOneTapOptionsThatActuallyOfferSomethingElse()
    {
        await using var db = NewDb();
        var llm = new FakeLlm
        {
            Proposals = new GapProposalSet
            {
                Proposals =
                {
                    new GapProposal
                    {
                        Group = "Thông báo / nhắc nhở",
                        Proposal = "Quản lý nhận thông báo khi có đơn mới.",
                        Options =
                        {
                            "Quản lý nhận thông báo khi có đơn mới.",  // trùng chính phương án
                            "  Chỉ nhân viên nhận thông báo  ",
                            "Chỉ nhân viên nhận thông báo",            // trùng mục trước
                            "   ",                                     // rỗng
                            "Dự án không có thông báo"
                        }
                    }
                }
            }
        };

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        var only = Assert.Single(outcome.Proposals);
        Assert.Equal(new[] { "Chỉ nhân viên nhận thông báo", "Dự án không có thông báo" }, only.Options);
    }

    // UI hiện mỗi nhóm như một câu hỏi CHỌN-MỘT với ba gợi ý đồng hạng: `Proposal` là gợi ý đầu, nên chỉ
    // còn đúng 2 chỗ cho lựa chọn thay thế. Model trả dư thì cắt ở server, không đẩy phần cắt sang cho
    // client — dòng thứ tư trở đi biến danh sách "liếc rồi bấm" thành một danh sách phải ĐỌC.
    [Fact]
    public async Task Propose_CapsAlternativesAtTwo_SoEachGroupOffersExactlyThreeChoices()
    {
        await using var db = NewDb();
        var llm = new FakeLlm
        {
            Proposals = new GapProposalSet
            {
                Proposals =
                {
                    new GapProposal
                    {
                        Group = "Thông báo / nhắc nhở",
                        Proposal = "Quản lý nhận thông báo khi có đơn mới.",
                        Options = { "Chỉ nhân viên nhận", "Cả hai cùng nhận", "Gửi cuối ngày", "Dự án không có thông báo" }
                    }
                }
            }
        };

        var outcome = await NewProposeSut(db, llm).ExecuteAsync(_projectId);

        var only = Assert.Single(outcome.Proposals);
        Assert.Equal(new[] { "Chỉ nhân viên nhận", "Cả hai cùng nhận" }, only.Options);
    }

    // ---- Bước 2: chốt ----

    [Fact]
    public async Task Confirm_WritesTheDecisionsAsAUserTurn_SoEveryDistillLayerSeesThem()
    {
        await using var db = NewDb();

        var outcome = await NewConfirmSut(db, new FakeLlm { CoverageReply = FullMap }).ExecuteAsync(_projectId, new[]
        {
            new GapDecision { Group = "Thông báo / nhắc nhở", Question = "Ai được báo?", Answer = "Quản lý nhận thông báo khi có đơn mới." },
            new GapDecision { Group = "Báo cáo / thống kê", Question = "Báo cáo nào?", Answer = "Trưởng phòng xem báo cáo tháng." }
        });

        Assert.Equal(ConfirmGapsStatus.Ok, outcome.Status);

        await using var verify = NewDb();
        var turns = await verify.AgentConversations.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync();
        var userTurn = turns[^2];
        Assert.Equal("user", userTurn.Role);
        Assert.Contains("Quản lý nhận thông báo khi có đơn mới.", userTurn.Message);
        Assert.Contains("Trưởng phòng xem báo cáo tháng.", userTurn.Message);
    }

    [Fact]
    public async Task Confirm_MergesTheCoverageMapImmediately_SoTheProgressPanelReactsAtOnce()
    {
        await using var db = NewDb();

        var outcome = await NewConfirmSut(db, new FakeLlm { CoverageReply = FullMap })
            .ExecuteAsync(_projectId, new[] { new GapDecision { Group = "Thông báo / nhắc nhở", Answer = "Quản lý nhận thông báo." } });

        Assert.True(outcome.Ready);
        Assert.Equal(4, outcome.Coverage.Count);
        Assert.All(outcome.Coverage, c => Assert.Equal("RÕ", c.Status));
        Assert.Equal(FullMap, (await NewDb().Projects.SingleAsync()).RequirementCoverageMap);
    }

    [Fact]
    public async Task Confirm_WhenTheMapIsFull_ClosesWithAWriteRequirementInvite()
    {
        await using var db = NewDb();

        await NewConfirmSut(db, new FakeLlm { CoverageReply = FullMap })
            .ExecuteAsync(_projectId, new[] { new GapDecision { Group = "Thông báo / nhắc nhở", Answer = "Quản lý nhận thông báo." } });

        var last = await LastTurnAsync();
        Assert.Equal("assistant", last.Role);
        Assert.True(RequirementReadinessGate.IsWriteRequirementInvite(last.Message));
    }

    // Cổng vẫn có thể chưa mở sau lượt chốt (distill thấy câu trả lời còn mơ hồ, hoặc lời gọi gộp lỗi ⇒
    // fail-closed). Lúc đó lượt BA TUYỆT ĐỐI không được mời bấm nút — nếu không, nút mờ mà BA lại bảo bấm.
    [Fact]
    public async Task Confirm_WhenTheMapIsStillIncomplete_DoesNotInviteWriteRequirement()
    {
        await using var db = NewDb();

        var outcome = await NewConfirmSut(db, new FakeLlm { CoverageReply = PartialMap })
            .ExecuteAsync(_projectId, new[] { new GapDecision { Group = "Thông báo / nhắc nhở", Answer = "Quản lý nhận thông báo." } });

        Assert.False(outcome.Ready);

        var last = await LastTurnAsync();
        Assert.Equal("assistant", last.Role);
        Assert.False(RequirementReadinessGate.IsWriteRequirementInvite(last.Message));
        // Câu hỏi dựng sẵn của gate phải nêu đúng nhóm còn thiếu, không phải một câu chặn chung chung.
        Assert.Contains("Báo cáo / thống kê", last.Message);
    }

    [Fact]
    public async Task Confirm_WithOnlyBlankAnswers_IsRejectedWithoutWritingAnything()
    {
        await using var db = NewDb();

        var outcome = await NewConfirmSut(db, new FakeLlm())
            .ExecuteAsync(_projectId, new[] { new GapDecision { Group = "Thông báo / nhắc nhở", Answer = "   " } });

        Assert.Equal(ConfirmGapsStatus.NoDecisions, outcome.Status);
        Assert.Equal(1, await NewDb().AgentConversations.CountAsync());
    }

    private async Task<AgentConversation> LastTurnAsync()
    {
        await using var verify = NewDb();
        return await verify.AgentConversations
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).FirstAsync();
    }

    private static ProposeRemainingGapsUseCase NewProposeSut(AppDbContext db, FakeLlm llm) =>
        new(db, new BAAgentResolver(db), new GapProposalService(db, llm, new StubPrompts()));

    private static ConfirmRemainingGapsUseCase NewConfirmSut(AppDbContext db, FakeLlm llm) =>
        new(db, new BAConversationLog(db), new BAAgentResolver(db),
            new RequirementCoverageService(db, llm, new StubPrompts()));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Fake ILlmClient phục vụ cả hai đường: structured (soạn phương án) và text (gộp bản đồ bao phủ).
    private sealed class FakeLlm : ILlmClient
    {
        public int StructuredCalls;
        public bool Fail;
        public GapProposalSet? Structured = new();
        public string RawContent = "{}";
        public string CoverageReply = "";

        // Đường "model có structured output": gán bộ phương án trả thẳng về.
        public GapProposalSet Proposals
        {
            set => Structured = value;
        }

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = !Fail, Content = Fail ? string.Empty : CoverageReply });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            StructuredCalls++;
            var result = new LlmCallResult { IsSuccess = !Fail, Content = Fail ? string.Empty : RawContent };
            return Task.FromResult((result, Fail ? null : Structured as T));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
