using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// "Bản đồ bao phủ yêu cầu" + "danh sách câu hỏi" per project: gộp các lượt chat MỚI (kể từ con trỏ) vào
// bảng trạng thái 12 nhóm và danh sách câu hỏi, trong MỘT lời gọi, lưu trên Project.RequirementCoverageMap
// và Project.OpenQuestions. Các test chốt: (1) không có lượt mới thì không gọi LLM, trả bản hiện hành;
// (2) có lượt mới thì gọi LLM một lần, ghi HAI cột + dời con trỏ (bền trong DB); (3) lời gọi lỗi thì THỬ
// LẠI một lần rồi fail-open — giữ bản cũ, KHÔNG dời con trỏ để lượt sau gộp bù, và báo cờ DistillFailed để
// người dùng thấy tiến độ đang cũ; (4) lần gọi sau chỉ gộp phần delta; (5) nhãn nhóm của câu hỏi được chốt
// về đúng một trong 12 nhãn checklist ngay ở đường ghi.
public class RequirementCoverageServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    public RequirementCoverageServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.SaveChanges();
    }

    [Fact]
    public async Task UpdateAndLoadAsync_NoNewTurns_DoesNotCallLlm_ReturnsCurrentMap()
    {
        var (project, ba) = await SeedAsync(turns: 0, existingMap: CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [RÕ] app kho"));
        var llm = new FakeLlm();

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var coverage = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(0, llm.Calls);
        Assert.Equal(new[] { "app kho" }, Assert.Single(CoverageMapParser.Parse(coverage.Map)).Known);
        Assert.False(coverage.DistillFailed);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_NewTurns_CallsLlmOnce_SavesMap_AndAdvancesPointer()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = CoverageMapFixture.DistillReply("- ★ Mục tiêu / bài toán: [MỘT PHẦN] còn thiếu: luồng chính") };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var coverage = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.False(coverage.DistillFailed);
        Assert.Equal(4, trackedProject.CoverageHarvestedTurnCount);

        // Bản đồ được LƯU dạng JSON — model trả về text của format cũ vẫn được đọc và chuẩn hoá sang JSON,
        // đó là đường nâng cấp cho cả model không nhận response_format lẫn dự án có bản đồ cũ trong DB.
        var row = Assert.Single(CoverageMapParser.Parse(coverage.Map));
        Assert.Equal("MỘT PHẦN", row.Status);
        Assert.True(row.IsCore);

        // Câu hỏi đi ra ở CỘT KHÁC, không nằm trong bản đồ — và đi kèm luôn trong kết quả trả về, vì lượt
        // gộp có thể chạy ở một DI scope riêng nên caller không đọc lại entity được.
        Assert.Equal("luồng chính", Assert.Single(coverage.Questions).Text);
        Assert.Equal("Mục tiêu / bài toán", coverage.Questions.Single().Group);
        Assert.DoesNotContain("luồng chính", coverage.Map, StringComparison.Ordinal);

        // Bền trong DB, không chỉ trên entity đang track.
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal(coverage.Map, reloaded.RequirementCoverageMap);
        Assert.Equal("luồng chính", InterviewOutlookParser.ParseOpenQuestions(reloaded.OpenQuestions).Single().Text);
        Assert.Equal(4, reloaded.CoverageHarvestedTurnCount);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_WhenLlmFails_FailsOpen_KeepsMapAndPointer()
    {
        var (project, ba) = await SeedAsync(turns: 4, existingMap: "bản đồ cũ", harvestedTurnCount: 2);
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var coverage = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        // Lỗi ⇒ THỬ LẠI đúng một lần trước khi chịu thua: bản đồ đứng im không chỉ làm trễ panel, nó khiến
        // BA dẫn lượt sau bằng bản đồ chưa có câu trả lời vừa rồi và hỏi lại đúng nhóm đó.
        Assert.Equal(2, llm.Calls);
        Assert.Equal("bản đồ cũ", coverage.Map);
        Assert.Equal(2, trackedProject.CoverageHarvestedTurnCount);
        // …và người dùng phải BIẾT bản đồ đang cũ, thay vì tự hỏi vì sao tiến độ không nhích.
        Assert.True(coverage.DistillFailed);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_SecondCallWithoutNewTurns_DoesNotCallLlmAgain()
    {
        var (project, ba) = await SeedAsync(turns: 3);
        var llm = new FakeLlm { Reply = CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [RÕ] App quản lý kho.") };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);
        var sut = NewSut(db, llm);

        await sut.UpdateAndLoadAsync(trackedProject, trackedBa, _model);
        var coverage = await sut.UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Equal(new[] { "App quản lý kho." }, Assert.Single(CoverageMapParser.Parse(coverage.Map)).Known);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_IncludesBaSuggestions_SoReferentialAnswerKeepsContext()
    {
        // Kịch bản bug gốc: BA hỏi kèm 3 gợi ý, user chọn option tham chiếu "Cả hai mục tiêu trên".
        // Khối hội thoại gộp phải chứa các option đã đưa ra, nếu không distill mất context.
        var ba = new Agent { Id = Guid.NewGuid(), Temperature = 0.2, AiModelId = _model.Id };
        var project = new Project { Id = Guid.NewGuid(), Name = "P" };
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = NewDb())
        {
            seed.Agents.Add(ba);
            seed.Projects.Add(project);
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = project.Id,
                AgentId = ba.Id,
                Role = "assistant",
                Message = "Mục tiêu cụ thể của ứng dụng này là gì?",
                Suggestions = "[\"Số hóa quy trình thủ công trên Excel\",\"Chuẩn hóa mẫu JD và quản lý phiên bản\",\"Cả hai mục tiêu trên\"]",
                CreatedAt = baseTime
            });
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = project.Id,
                AgentId = ba.Id,
                Role = "user",
                Message = "Cả hai mục tiêu trên",
                CreatedAt = baseTime.AddSeconds(1)
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm { Reply = CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [RÕ] App quản lý kho.") };
        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.NotNull(llm.LastUserMessage);
        // Cả hai option "thực" phải xuất hiện để "Cả hai mục tiêu trên" nối được về đúng nội dung.
        Assert.Contains("Số hóa quy trình thủ công trên Excel", llm.LastUserMessage);
        Assert.Contains("Chuẩn hóa mẫu JD và quản lý phiên bản", llm.LastUserMessage);
        Assert.Contains("Cả hai mục tiêu trên", llm.LastUserMessage);
    }

    [Fact]
    public async Task UpdateAndLoadAsync_IncludesSourceFileText_SoMapCreditsAttachedDocs()
    {
        // Bản đồ là nguồn chân lý của cổng "Write Requirement" ⇒ distill phải thấy text tài liệu nguồn,
        // nếu không bản đồ treo [CHƯA HỎI] những thứ tài liệu đính kèm đã trả lời và cổng chặn oan.
        var (project, ba) = await SeedAsync(turns: 2);
        await using (var seed = NewDb())
        {
            seed.ProjectSourceFiles.Add(new ProjectSourceFile
            {
                ProjectId = project.Id,
                FileName = "quy-trinh-duyet.pdf",
                ContentType = "application/pdf",
                ExtractedText = "Quy trình duyệt: nhân viên gửi đơn, trưởng phòng duyệt trong 2 ngày."
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm { Reply = CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [RÕ] App quản lý kho.") };
        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.NotNull(llm.LastUserMessage);
        Assert.Contains("quy-trinh-duyet.pdf", llm.LastUserMessage);
        Assert.Contains("trưởng phòng duyệt trong 2 ngày", llm.LastUserMessage);
    }

    // Dòng «Thông báo / nhắc nhở» kẹt [MỘT PHẦN] với một mẩu "còn thiếu" cũ trong khi bảng thông báo đã
    // nằm trong DB — ca thật ở dự án "JD Libary 7". Bảng đã chốt là bằng chứng TẤT ĐỊNH, không phải thứ để
    // trông chờ distiller đọc hộ, nên đường ghi phải tự sửa dòng đó (xem CoverageConfirmedTableGuard).
    [Fact]
    public async Task UpdateAndLoadAsync_RaisesTheNotificationRow_WhenItsTableIsAlreadyConfirmed()
    {
        var (project, ba) = await SeedAsync(turns: 2);
        await using (var seed = NewDb())
        {
            var p = await seed.Projects.FirstAsync(x => x.Id == project.Id);
            p.NotificationMap = """
                [
                  { "entity": "JD", "event": "Chờ HRBP duyệt", "needed": true, "to": ["HRBP"], "cc": ["Manager của orgUnit"] },
                  { "entity": "JD", "event": "Được tạo", "needed": false, "to": [] }
                ]
                """;
            await seed.SaveChangesAsync();
        }

        // Lượt distill trả về đúng dòng tự mâu thuẫn của ca thật: vừa nói đã chốt vừa nói chưa rõ.
        var llm = new FakeLlm
        {
            Reply = CoverageMapFixture.DistillReply("- Thông báo / nhắc nhở: [MỘT PHẦN] Đã chốt To/CC riêng từng sự kiện. "
                + "còn thiếu: Chưa rõ người nhận cho từng sự kiện thông báo")
        };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var coverage = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.NotNull(coverage.Map);
        var row = Row(coverage.Map, "Thông báo");
        Assert.Equal("RÕ", row.Status);
        // …và câu hỏi chết của nhóm ấy bị dọn: BA bị cấm hỏi lẻ nó, nên để lại là để một câu không ai đóng
        // được, và CoveragePendingGuard sẽ hạ ngay dòng vừa nâng.
        Assert.DoesNotContain(coverage.Questions, q => q.IsOpen);

        // Bản đã sửa là bản được LƯU: cổng readiness, panel tiến độ và các cổng bảng đọc cùng một sự thật.
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal("RÕ", Row(reloaded.RequirementCoverageMap, "Thông báo").Status);
    }

    // Người dùng bị kẹt thì không gõ thêm gì cả — họ bấm gửi lại, hoặc tải lại trang. Lượt không có gì mới
    // vẫn phải gỡ được bản đồ kẹt, nếu không lối thoát duy nhất lại chính là lượt chat đang bị chặn.
    [Fact]
    public async Task UpdateAndLoadAsync_RepairsAStuckMap_EvenWithNoNewTurns_WithoutCallingLlm()
    {
        var (project, ba) = await SeedAsync(
            turns: 0,
            existingMap: CoverageMapFixture.Map("- Thông báo / nhắc nhở: [MỘT PHẦN] Email theo sự kiện."),
            existingQuestions: CoverageMapFixture.StoredQuestions(
                "- Thông báo / nhắc nhở: [MỘT PHẦN] còn thiếu: Chưa rõ người nhận cho từng sự kiện thông báo"));
        await using (var seed = NewDb())
        {
            var p = await seed.Projects.FirstAsync(x => x.Id == project.Id);
            p.NotificationMap = """[ { "entity": "JD", "event": "Available", "needed": true, "to": ["Manager của orgUnit"] } ]""";
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm();
        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var coverage = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(0, llm.Calls);
        Assert.Equal("RÕ", Row(coverage.Map, "Thông báo").Status);
    }

    // Danh sách câu hỏi hiện có được echo lại cho chính lượt distill: đây là một phép GỘP LŨY TIẾN, nên
    // model phải thấy bản cũ mới giữ được thứ các lượt trước đã chắt — mất khối này là mất cả danh sách sau
    // đúng một lượt.
    [Fact]
    public async Task UpdateAndLoadAsync_EchoesTheExistingQuestions_ToTheDistillTurn()
    {
        var (project, ba) = await SeedAsync(turns: 2);
        var llm = new FakeLlm();

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);
        trackedProject.OpenQuestions = InterviewOutlookParser.SerializeOpenQuestions(new[]
        {
            new OpenQuestionEntry
            {
                Group = "Vòng đời & trạng thái",
                Text = "chưa rõ kết quả Complete dùng để chuyển bước nào"
            }
        });

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Contains("Danh sách câu hỏi hiện có", llm.LastUserMessage, StringComparison.Ordinal);
        // Kèm nhãn nhóm: distiller phải giữ mục ở ĐÚNG nhóm, không phải đoán nhóm lần thứ hai.
        Assert.Contains("[Vòng đời & trạng thái] chưa rõ kết quả Complete", llm.LastUserMessage, StringComparison.Ordinal);
    }

    // Chưa có câu hỏi nào ⇒ không nhồi một tiêu đề rỗng vào prompt của mọi lượt chat.
    [Fact]
    public async Task UpdateAndLoadAsync_OmitsTheQuestionBlock_WhenThereIsNothingYet()
    {
        var (project, ba) = await SeedAsync(turns: 2);
        var llm = new FakeLlm();

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.DoesNotContain("Danh sách câu hỏi hiện có", llm.LastUserMessage, StringComparison.Ordinal);
    }

    // Nhãn nhóm của một câu hỏi do MODEL điền, nhưng nó là đầu vào của bốn chốt chặn TẤT ĐỊNH — nên nó
    // phải được chốt về đúng một trong 12 nhãn checklist NGAY Ở ĐƯỜNG GHI, chứ không để mỗi tầng đọc tự
    // đoán lấy. Model viết gọn ("Luồng ngoại lệ" cho «Luồng ngoại lệ & trường hợp đặc biệt») ⇒ vẫn khớp.
    [Fact]
    public async Task AGroupWrittenLoosely_IsSnappedToTheChecklistLabel()
    {
        var stored = await HarvestQuestionAsync(new OpenQuestionEntry
        {
            Group = "Luồng ngoại lệ",
            Text = "Chưa rõ đăng ký lại sau khi bị Reject"
        });

        var item = Assert.Single(InterviewOutlookParser.ParseOpenQuestions(stored));
        Assert.Equal("Luồng ngoại lệ & trường hợp đặc biệt", item.Group);
        Assert.Equal("Chưa rõ đăng ký lại sau khi bị Reject", item.Text);
    }

    // Nhãn model tự nghĩ ra không khớp nhóm nào ⇒ để RỖNG. Fail-open: mục vẫn nằm trong danh sách để BA
    // hỏi, chỉ không hạ được dòng bản đồ nào — guard không được phép hạ nhầm vì một nhãn vô nghĩa.
    [Fact]
    public async Task AGroupThatMatchesNothing_IsBlanked_ButTheQuestionSurvives()
    {
        var stored = await HarvestQuestionAsync(new OpenQuestionEntry
        {
            Group = "Tích hợp hệ thống ngoài",
            Text = "Chưa rõ nối với SAP kiểu gì"
        });

        var item = Assert.Single(InterviewOutlookParser.ParseOpenQuestions(stored));
        Assert.Equal(string.Empty, item.Group);
        Assert.Equal("Chưa rõ nối với SAP kiểu gì", item.Text);
    }

    // Mục ĐÃ TRẢ LỜI ở lại danh sách thay vì bị xoá: lượt distill chỉ thấy các lượt MỚI, nên một câu đã đóng
    // từ mười lượt trước mà biến mất khỏi đầu vào là một câu nó dựng lại y nguyên — và người dùng bị hỏi
    // lại điều họ đã nói. Nó phải ở lại DB, và phải KHÔNG được đọc thành một câu hỏi còn treo.
    [Fact]
    public async Task AnAnsweredQuestion_StaysStored_ButNeverCountsAsOpen()
    {
        var stored = await HarvestQuestionAsync(new OpenQuestionEntry
        {
            Group = "Quy trình hiện tại & điểm khó",
            Text = "các bước của quy trình Excel hiện tại",
            Status = OpenQuestionEntry.Answered,
            Answer = "mỗi tháng HR gửi file, mình lọc tay"
        });

        var item = Assert.Single(InterviewOutlookParser.ParseOpenQuestions(stored));
        Assert.False(item.IsOpen);
        Assert.Equal("mỗi tháng HR gửi file, mình lọc tay", item.Answer);
        // …và nó không được đi vào ngữ cảnh chat của BA, cũng không gắn vào dòng bản đồ nào.
        Assert.Empty(InterviewOutlookParser.ToText(new[] { item }));
    }

    // ── Trần độ dài của bản đồ (Cap) ─────────────────────────────────────────────────────────────────

    // Phép cắt cũ chia đôi trường dài nhất cho tới khi vừa trần, nên nó đẻ ra đúng những dòng người dùng
    // đọc thấy trên panel: một mẩu cụt giữa từ ("…mỗi nhân viên s"). Một mẩu như thế không rà được, mà
    // nhánh PHÁT LẠI của cổng readiness lại hỏi đúng một câu về nó.
    [Fact]
    public async Task UpdateAndLoadAsync_OverlongKnownItem_IsClippedAtAWordBoundary()
    {
        var word = "nhânviên ";
        var stored = await DistillAsync(new CoverageMapEntry
        {
            Label = "Mục tiêu / bài toán", Core = true, Status = "RÕ",
            Known = { string.Concat(Enumerable.Repeat(word, 200)).Trim() }
        });

        var known = Assert.Single(Assert.Single(CoverageMapParser.Parse(stored)).Known);
        Assert.EndsWith("nhânviên…", known, StringComparison.Ordinal);
        Assert.DoesNotContain("nhânviê…", known, StringComparison.Ordinal);
    }

    // Bản đồ quá trần thì BỎ NGUYÊN mẩu cũ nhất của dòng nhiều mẩu nhất, không xén mẩu nào giữa chừng —
    // và không bao giờ chạm dòng chỉ còn một mẩu, vì bỏ nó là xoá trắng phần đã ghi nhận của cả một nhóm.
    [Fact]
    public async Task UpdateAndLoadAsync_MapOverTheCap_DropsWholeItems_AndNeverEmptiesARow()
    {
        var filler = string.Concat(Enumerable.Repeat("dữ liệu nghiệp vụ ", 20)).Trim();
        var fat = new CoverageMapEntry { Label = "Mục tiêu / bài toán", Core = true, Status = "RÕ" };
        for (var i = 0; i < 40; i++)
            fat.Known.Add($"Ghi nhận {i}: {filler}");

        var slim = new CoverageMapEntry
        {
            Label = "Quy mô sử dụng", Status = "RÕ",
            Known = { $"Chỉ một mẩu duy nhất: {filler}" }
        };

        var items = CoverageMapParser.Parse(await DistillAsync(fat, slim));

        Assert.True(CoverageMapParser.Serialize(items).Length <= 8000);
        // Dòng một mẩu còn NGUYÊN VĂN: nó không phải chỗ để lấy chỗ trống.
        Assert.Equal(slim.Known, items[1].Known);
        // Dòng béo mất các mẩu CŨ NHẤT, các mẩu còn lại không bị xén.
        Assert.NotEmpty(items[0].Known);
        Assert.EndsWith($"Ghi nhận 39: {filler}", items[0].Known[^1], StringComparison.Ordinal);
        Assert.All(items[0].Known, k => Assert.DoesNotContain("…", k, StringComparison.Ordinal));
    }

    // Một lượt chắt lọc trả về mảng RỖNG cho một dòng đang đầy thì không ai thấy lỗi nào, chỉ có tiến độ
    // khai thác lặng lẽ mất một nhóm. CoverageKnownLossGuard đứng ĐẦU chuỗi guard của đường ghi cho đúng
    // ca đó; đây là chốt chặn cho chỗ nó được CẮM VÀO, còn ranh giới của nó ở CoverageKnownLossGuardTests.
    [Fact]
    public async Task UpdateAndLoadAsync_DistillWipesAKnownRow_KeepsWhatWasAlreadyRecorded()
    {
        var (project, ba) = await SeedAsync(turns: 2,
            existingMap: CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [RÕ] App quản lý khóa học bắt buộc."));
        var llm = new FakeLlm
        {
            Structured = new CoverageDistillDocument
            {
                Items = { new CoverageMapEntry { Label = "Mục tiêu / bài toán", Core = true, Status = "RÕ" } }
            }
        };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var coverage = await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        Assert.Equal(new[] { "App quản lý khóa học bắt buộc." },
            Assert.Single(CoverageMapParser.Parse(coverage.Map)).Known);
    }

    // Chạy MỘT lượt distill trả về đúng các dòng đưa vào, rồi trả bản đồ đã lưu.
    private async Task<string?> DistillAsync(params CoverageMapEntry[] entries)
    {
        var (project, ba) = await SeedAsync(turns: 2);
        var llm = new FakeLlm { Structured = new CoverageDistillDocument { Items = entries.ToList() } };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        return (await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model)).Map;
    }

    private async Task<string?> HarvestQuestionAsync(OpenQuestionEntry question)
    {
        var (project, ba) = await SeedAsync(turns: 2);
        var llm = new FakeLlm
        {
            Structured = new CoverageDistillDocument
            {
                Items = { new CoverageMapEntry { Label = "Mục tiêu / bài toán", Core = true, Status = "MỘT PHẦN", Known = { "app kho" } } },
                Questions = { question }
            }
        };

        await using var db = NewDb();
        var trackedProject = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).UpdateAndLoadAsync(trackedProject, trackedBa, _model);

        return (await NewDb().Projects.FirstAsync(p => p.Id == project.Id)).OpenQuestions;
    }

    private static ICOGenerator.Contracts.Requirements.CoverageMapItem Row(string? map, string labelPrefix) =>
        CoverageMapParser.Parse(map).First(x => x.Label.StartsWith(labelPrefix, StringComparison.Ordinal));

    private RequirementCoverageService NewSut(AppDbContext db, ILlmClient llm)
    {
        var prompts = new StubPrompts();
        return new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts));
    }

    private async Task<(Project Project, Agent Ba)> SeedAsync(int turns, string? existingMap = null, int harvestedTurnCount = 0, string? existingQuestions = null)
    {
        var ba = new Agent { Id = Guid.NewGuid(), Temperature = 0.2, AiModelId = _model.Id };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            RequirementCoverageMap = existingMap,
            OpenQuestions = existingQuestions,
            CoverageHarvestedTurnCount = harvestedTurnCount
        };

        await using var db = NewDb();
        db.Agents.Add(ba);
        db.Projects.Add(project);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < turns; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = project.Id,
                AgentId = ba.Id,
                Role = i % 2 == 0 ? "user" : "assistant",
                Message = $"turn-{i}",
                CreatedAt = baseTime.AddSeconds(i)
            });
        }
        await db.SaveChangesAsync();
        return (project, ba);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Fake ILlmClient: chỉ phục vụ đường gộp bản đồ (ChatWithLogAsync). Đếm số lần gọi và trả/đẩy lỗi theo cấu hình.
    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = CoverageMapFixture.DistillReply("- ★ Mục tiêu / bài toán: [RÕ] App quản lý kho.");
        public bool Fail;

        // Text của lượt user cuối (chính là khối hội thoại được gộp) để test soi xem gợi ý có được đính kèm không.
        public string? LastUserMessage;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            return Task.FromResult(new LlmCallResult
            {
                IsSuccess = !Fail,
                Content = Fail ? string.Empty : Reply,
                ErrorMessage = Fail ? "boom" : null
            });
        }

        // Lượt distill bản đồ đi qua đường structured output. Trả Value null ⇒ service parse Content như
        // văn xuôi, nên Reply của từng test vẫn là bản đồ dạng text và các assert giữ nguyên ý nghĩa.
        // Structured != null ⇒ đi đường structured output THẬT (model nhận response_format); null ⇒ service
        // parse Content như văn xuôi, đường lùi cho model không nhận response_format.
        public CoverageDistillDocument? Structured;

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult((ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).Result, Structured as T));
    }

    // Prompt bao phủ THẬT: phép chốt nhãn nhóm (Canonicalize) chạy trên đúng 12 nhãn mà production bóc ra
    // từ file này, không phải một danh sách chép tay trong test.
    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }

        public override string Get(string relativePath)
        {
            var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);

            var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts", relative);
            if (File.Exists(fromBin))
                return File.ReadAllText(fromBin);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Prompts", relative)))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir!.FullName, "Prompts", relative));
        }
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
