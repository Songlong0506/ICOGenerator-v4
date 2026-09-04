using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

public class CoverageMapParserTests
{
    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(CoverageMapParser.Parse(null));
        Assert.Empty(CoverageMapParser.Parse("   "));
    }

    [Fact]
    public void Parse_StandardMap_ReadsStatusCoreAndSummary()
    {
        var map = CoverageMapFixture.Map("""
            - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép
            - ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] Nhân viên + quản lý; còn thiếu: admin?
            - Báo cáo / thống kê: [CHƯA HỎI]
            - Phân quyền theo nghiệp vụ: [KHÔNG ÁP DỤNG] ứng dụng cá nhân
            """);

        var items = CoverageMapParser.Parse(map);

        Assert.Equal(4, items.Count);
        Assert.True(items[0].IsCore);
        Assert.Equal("Mục tiêu / bài toán", items[0].Label);
        Assert.Equal("RÕ", items[0].Status);
        // Dấu chấm là của Summary, không phải của bản đồ: CoverageMapItem.KnownText đóng câu từng mẩu
        // lúc ghép (hai mẩu nối trần thì dính vào nhau thành một câu vô nghĩa ngay trong lời phát lại mà
        // người dùng phải rà). Nội dung lưu vẫn đúng nguyên văn model viết — xem ToText.
        Assert.Equal("Quản lý đơn nghỉ phép.", items[0].Summary);
        Assert.Equal("MỘT PHẦN", items[1].Status);
        Assert.False(items[2].IsCore);
        Assert.Equal("CHƯA HỎI", items[2].Status);
        Assert.Equal("KHÔNG ÁP DỤNG", items[3].Status);
    }

    [Fact]
    public void Parse_IgnoresProseLinesAroundMap()
    {
        var map = CoverageMapFixture.Map("Đây là bản đồ:\n- ★ Mục tiêu / bài toán: [RÕ] ok\nHết.");

        var items = CoverageMapParser.Parse(map);

        Assert.Single(items);
    }

    // Mẫu số KHÔNG rút theo [KHÔNG ÁP DỤNG]: thước đo phải đứng yên suốt cuộc phỏng vấn, nếu không con số
    // đang chạy tự nhảy mốc ("0/12" → "1/9") mà người dùng không hiểu vì sao.
    [Fact]
    public void Progress_KeepsTotalAsDenominatorAndCountsNotApplicable()
    {
        var items = CoverageMapParser.Parse(CoverageMapFixture.Map("""
            - A: [RÕ] x
            - B: [MỘT PHẦN] y
            - C: [KHÔNG ÁP DỤNG] z
            """));

        var progress = CoverageMapParser.Progress(items);

        Assert.Equal(1, progress.Clear);
        Assert.Equal(2, progress.Applicable);
        Assert.Equal(3, progress.Total);
        Assert.Equal(1, progress.NotApplicable);
    }

    // Bất biến "thanh đầy ⇔ nút Write Requirement mở khoá": cổng readiness chỉ đòi mọi dòng ÁP DỤNG lên
    // [RÕ], nên nhóm [KHÔNG ÁP DỤNG] phải tính là đã xong — không thì thanh mãi không đầy trong khi nút
    // đã sáng, đúng kiểu lệch mà cả UI lẫn tài liệu đang khẳng định là không thể xảy ra.
    [Fact]
    public void Percent_FullWhenEveryApplicableGroupIsClear()
    {
        var map = CoverageMapFixture.Map("""
            - A: [RÕ] x
            - B: [RÕ] y
            - C: [KHÔNG ÁP DỤNG] z
            """);
        var items = CoverageMapParser.Parse(map);

        Assert.Equal(100, CoverageMapParser.Progress(items).Percent);
        Assert.True(RequirementReadinessGate.IsReady(map));
    }

    [Fact]
    public void Percent_ZeroForEmptyMapAndFreshChecklist()
    {
        Assert.Equal(0, CoverageMapParser.Progress(Array.Empty<CoverageMapItem>()).Percent);
        Assert.Equal(0, CoverageMapParser.Progress(CoverageMapParser.Parse("- A: [CHƯA HỎI]")).Percent);
    }

    // ── Format JSON: hình dạng LƯU TRỮ của bản đồ ─────────────────────────────────────────────────────

    // Bốn trường bậc nhất phải đi hết một vòng ghi–đọc mà không mất gì. Đây là bất biến mà cả bốn guard
    // dựa vào: chúng parse → gán thuộc tính → serialize, nên một trường rơi rụng ở vòng này là một trường
    // rơi rụng ở MỌI lượt chat.
    [Fact]
    public void SerializeThenParse_RoundTripsEveryField()
    {
        var original = new List<CoverageMapItem>
        {
            new()
            {
                Label = "Mục tiêu / bài toán", IsCore = true, Status = "MỘT PHẦN",
                Known = new List<string> { "Quản lý đơn nghỉ phép.", "Không phải trưởng phòng duyệt đâu." }
            },
            new() { Label = "Báo cáo / thống kê", Status = "KHÔNG ÁP DỤNG", Known = new List<string> { "Người dùng nói không cần." } }
        };

        var items = CoverageMapParser.Parse(CoverageMapParser.Serialize(original));

        Assert.Equal(2, items.Count);
        Assert.Equal("Mục tiêu / bài toán", items[0].Label);
        Assert.True(items[0].IsCore);
        Assert.Equal("MỘT PHẦN", items[0].Status);
        Assert.Equal(new[] { "Quản lý đơn nghỉ phép.", "Không phải trưởng phòng duyệt đâu." }, items[0].Known);

        Assert.Equal("KHÔNG ÁP DỤNG", items[1].Status);
        Assert.False(items[1].IsCore);

        // CÂU HỎI không nằm trong bản đồ: nó có cột riêng, và được gắn vào dòng ở đường ĐỌC.
        Assert.DoesNotContain("còn thiếu", CoverageMapParser.Serialize(original), StringComparison.Ordinal);
    }

    // Bản đồ toàn tiếng Việt và nó đi vào prompt ở MỌI lượt chat. Mặc định của System.Text.Json biến mỗi
    // chữ có dấu thành \uXXXX — dài gấp ~6 lần cho đúng một nội dung.
    [Fact]
    public void Serialize_DoesNotEscapeVietnamese()
    {
        var json = CoverageMapParser.Serialize(new List<CoverageMapItem>
        {
            new() { Label = "Vòng đời & trạng thái", Status = "RÕ", Known = new List<string> { "Đơn khoá sau khi duyệt." } }
        });

        Assert.Contains("Vòng đời & trạng thái", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }

    // JSON hỏng cú pháp (model bị cắt giữa chừng) ⇒ coi như chưa có bản đồ, không ném lên khung chat:
    // panel ẩn, cổng readiness nói chưa tổng hợp được, lượt sau gộp bù.
    [Fact]
    public void Parse_BrokenJson_FailsOpenToEmpty()
    {
        Assert.Empty(CoverageMapParser.Parse("{\"items\":[{\"label\":\"Mục tiêu\",\"stat"));
        Assert.Empty(CoverageMapParser.Parse("{}"));
    }

    // ToText dựng lại đúng 12 dòng mà BA đọc trong ngữ cảnh chat (bản đồ lưu JSON, nạp vào prompt dạng
    // bullet — xem BAChatPromptBlocks.CoverageMap). Mất khối ở đây là mất bằng chứng khỏi cả
    // ngữ cảnh chat lẫn bản xuất hội thoại.
    [Fact]
    public void ToText_RendersTheBulletFormTheBaReads()
    {
        const string bullet = "- ★ Mục tiêu / bài toán: [MỘT PHẦN] Quản lý đơn. còn thiếu: ai duyệt";
        // Câu hỏi phải được GẮN vào trước khi dựng bullet — chúng nằm ở cột khác, xem AttachQuestions.
        var text = CoverageMapParser.ToText(CoverageMapParser.AttachQuestions(
            CoverageMapParser.Parse(CoverageMapFixture.Map(bullet)), CoverageMapFixture.Questions(bullet)));

        Assert.Equal(
            "- ★ Mục tiêu / bài toán: [MỘT PHẦN] Quản lý đơn. còn thiếu: ai duyệt",
            text);
    }

    // Summary là thứ panel tiến độ hiển thị: nó phải ghép lại đúng phần đã ghi nhận + các câu còn phải
    // hỏi, để đổi chỗ lưu câu hỏi không đổi một pixel nào trên màn hình.
    [Fact]
    public void Summary_JoinsKnownAndQuestions_ForTheProgressPanel()
    {
        Assert.Equal("Quản lý đơn. còn thiếu: ai duyệt",
            new CoverageMapItem { Known = new[] { "Quản lý đơn." }, Questions = new[] { "ai duyệt" } }.Summary);
        Assert.Equal("Quản lý đơn.", new CoverageMapItem { Known = new[] { "Quản lý đơn." } }.Summary);
        Assert.Equal("còn thiếu: ai duyệt", new CoverageMapItem { Questions = new[] { "ai duyệt" } }.Summary);
        Assert.Empty(new CoverageMapItem().Summary);

        // Một nhóm được phép có NHIỀU câu hỏi — ô nextQuestion cũ chỉ chứa được một, nên prompt phải dặn
        // gộp chúng thành một câu, đúng hình dạng câu hỏi kép mà phía chat cấm.
        Assert.Equal("Quản lý đơn. còn thiếu: ai duyệt; duyệt trong mấy ngày",
            new CoverageMapItem { Known = new[] { "Quản lý đơn." }, Questions = new[] { "ai duyệt", "duyệt trong mấy ngày" } }.Summary);
    }

    // Câu hỏi ĐÃ TRẢ LỜI không bao giờ được gắn vào dòng bản đồ: dòng chỉ hiện điều CÒN PHẢI HỎI, và một
    // mục đã đóng hiện lên panel là mời người dùng trả lời lại thứ họ vừa nói.
    [Fact]
    public void AttachQuestions_SkipsAnsweredOnes()
    {
        var items = CoverageMapParser.AttachQuestions(
            CoverageMapParser.Parse(CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [MỘT PHẦN] Quản lý đơn.")),
            new[]
            {
                new OpenQuestionEntry { Group = "Mục tiêu / bài toán", Text = "ai duyệt" },
                OpenQuestionFixture.Answered("[Mục tiêu / bài toán] duyệt trong mấy ngày", "2 ngày")
            });

        Assert.Equal("ai duyệt", Assert.Single(items[0].Questions));
    }
    // ── `known` là DANH SÁCH ──────────────────────────────────────────────────────────────────────────

    // Bản đồ của MỌI dự án đang dở dang có `known` ở dạng chuỗi. Không đọc được nó thì lần đọc đầu sau khi
    // deploy trả về bản đồ RỖNG, và lượt chắt lọc kế tiếp dựng lại bản đồ chỉ từ vài lượt mới — cả buổi
    // phỏng vấn đã khai thác biến mất mà không ai thấy lỗi nào. Xem CoverageKnownJsonConverter.
    [Fact]
    public void Parse_LegacyStringKnown_ReadsItAsOneItem()
    {
        var items = CoverageMapParser.Parse(
            """{"items":[{"label":"Mục tiêu / bài toán","core":true,"status":"RÕ","known":"App quản lý kho.","evidence":"mình cần app quản lý kho"}]}""");

        var item = Assert.Single(items);
        Assert.Equal("RÕ", item.Status);
        Assert.Equal(new[] { "App quản lý kho." }, item.Known);
    }

    // Chuỗi RỖNG của bản đồ cũ là "chưa ghi nhận gì", không phải một mẩu rỗng: một phần tử "" lọt vào
    // danh sách thì mọi phép đếm phần tử (CoverageKnownLossGuard, Cap) đọc dòng đó thành "đang có nội dung".
    [Fact]
    public void Parse_LegacyEmptyStringKnown_ReadsAsEmptyList()
    {
        var items = CoverageMapParser.Parse(
            """{"items":[{"label":"Quy mô sử dụng","core":false,"status":"CHƯA HỎI","known":""}]}""");

        Assert.Empty(Assert.Single(items).Known);
    }

    // Khối bản đồ mà MODEL đọc phải thấy ranh giới từng mẩu: nối trần thì lượt gộp kế tiếp đọc hai ý
    // thành một câu và một trong hai biến mất. Văn xuôi (Summary) là chuyện của người đọc, không phải
    // của khối này.
    [Fact]
    public void ToText_SeparatesKnownItems()
    {
        var text = CoverageMapParser.ToText(new[]
        {
            new CoverageMapItem
            {
                Label = "Mục tiêu / bài toán", IsCore = true, Status = "RÕ",
                Known = new[] { "App quản lý kho", "Chỉ dùng trong nhà máy" }
            }
        });

        Assert.Equal("- ★ Mục tiêu / bài toán: [RÕ] App quản lý kho | Chỉ dùng trong nhà máy", text);
    }

    // Ngược lại: phần đọc cho NGƯỜI (panel tiến độ, lời phát lại của cổng readiness) là văn xuôi, mỗi mẩu
    // được đóng câu — hai mẩu dính vào nhau thành một câu vô nghĩa ngay trong thứ người dùng phải rà.
    [Fact]
    public void KnownText_ReadsAsProse()
    {
        Assert.Equal("App quản lý kho. Chỉ dùng trong nhà máy.",
            new CoverageMapItem { Known = new[] { "App quản lý kho", "Chỉ dùng trong nhà máy." } }.KnownText);
    }
}
