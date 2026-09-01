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
        var map = """
            - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép
            - ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] Nhân viên + quản lý; còn thiếu: admin?
            - Báo cáo / thống kê: [CHƯA HỎI]
            - Phân quyền theo nghiệp vụ: [KHÔNG ÁP DỤNG] ứng dụng cá nhân
            """;

        var items = CoverageMapParser.Parse(map);

        Assert.Equal(4, items.Count);
        Assert.True(items[0].IsCore);
        Assert.Equal("Mục tiêu / bài toán", items[0].Label);
        Assert.Equal("RÕ", items[0].Status);
        Assert.Equal("Quản lý đơn nghỉ phép", items[0].Summary);
        Assert.Equal("MỘT PHẦN", items[1].Status);
        Assert.False(items[2].IsCore);
        Assert.Equal("CHƯA HỎI", items[2].Status);
        Assert.Equal("KHÔNG ÁP DỤNG", items[3].Status);
    }

    [Fact]
    public void Parse_IgnoresProseLinesAroundMap()
    {
        var map = "Đây là bản đồ:\n- ★ Mục tiêu / bài toán: [RÕ] ok\nHết.";

        var items = CoverageMapParser.Parse(map);

        Assert.Single(items);
    }

    // Mẫu số KHÔNG rút theo [KHÔNG ÁP DỤNG]: thước đo phải đứng yên suốt cuộc phỏng vấn, nếu không con số
    // đang chạy tự nhảy mốc ("0/12" → "1/9") mà người dùng không hiểu vì sao.
    [Fact]
    public void Progress_KeepsTotalAsDenominatorAndCountsNotApplicable()
    {
        var items = CoverageMapParser.Parse("""
            - A: [RÕ] x
            - B: [MỘT PHẦN] y
            - C: [KHÔNG ÁP DỤNG] z
            """);

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
        var map = """
            - A: [RÕ] x
            - B: [RÕ] y
            - C: [KHÔNG ÁP DỤNG] z
            """;
        var items = CoverageMapParser.Parse(map);

        Assert.Equal(100, CoverageMapParser.Progress(items).Percent);
        Assert.True(RequirementReadinessGate.Evaluate(map).Ready);
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
                Known = "Quản lý đơn nghỉ phép.", Gap = "ai duyệt thay trưởng phòng",
                Evidence = "\"không phải trưởng phòng duyệt đâu\""
            },
            new() { Label = "Báo cáo / thống kê", Status = "KHÔNG ÁP DỤNG", Known = "Người dùng nói không cần." }
        };

        var items = CoverageMapParser.Parse(CoverageMapParser.Serialize(original));

        Assert.Equal(2, items.Count);
        Assert.Equal("Mục tiêu / bài toán", items[0].Label);
        Assert.True(items[0].IsCore);
        Assert.Equal("MỘT PHẦN", items[0].Status);
        Assert.Equal("Quản lý đơn nghỉ phép.", items[0].Known);
        Assert.Equal("ai duyệt thay trưởng phòng", items[0].Gap);
        Assert.Equal("\"không phải trưởng phòng duyệt đâu\"", items[0].Evidence);

        Assert.Equal("KHÔNG ÁP DỤNG", items[1].Status);
        Assert.False(items[1].IsCore);
        Assert.Empty(items[1].Gap);
    }

    // Bản đồ toàn tiếng Việt và nó đi vào prompt ở MỌI lượt chat. Mặc định của System.Text.Json biến mỗi
    // chữ có dấu thành \uXXXX — dài gấp ~6 lần cho đúng một nội dung.
    [Fact]
    public void Serialize_DoesNotEscapeVietnamese()
    {
        var json = CoverageMapParser.Serialize(new List<CoverageMapItem>
        {
            new() { Label = "Vòng đời & trạng thái", Status = "RÕ", Known = "Đơn khoá sau khi duyệt." }
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

    // ── Tương thích ngược: bản đồ dạng text còn nằm trong DB ──────────────────────────────────────────

    // Dự án tạo trước lần đổi format vẫn phải đọc được, và phần "còn thiếu:" nhồi trong tóm tắt phải tách
    // đúng ra trường Gap — nếu không, cổng readiness mất câu chặn của mọi dự án cũ trong đúng một lần deploy.
    [Fact]
    public void Parse_LegacyText_SplitsKnownFromGapAndEvidence()
    {
        var items = CoverageMapParser.Parse(
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] Có 3 vai trò. còn thiếu: mỗi vai trò làm được gì "
            + "{nguồn: \"nhân viên, quản lý, HR\"}");

        var item = Assert.Single(items);
        Assert.True(item.IsCore);
        Assert.Equal("MỘT PHẦN", item.Status);
        Assert.Equal("Có 3 vai trò.", item.Known);
        Assert.Equal("mỗi vai trò làm được gì", item.Gap);
        Assert.Equal("\"nhân viên, quản lý, HR\"", item.Evidence);
    }

    // Đường nâng cấp: đọc bản đồ cũ rồi ghi lại là đã sang JSON, không cần một bước migration nào chạm DB.
    [Fact]
    public void LegacyText_UpgradesToJson_WithoutLosingAnything()
    {
        const string legacy = "- ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép. {nguồn: \"app xin nghỉ\"}";

        var json = CoverageMapParser.Serialize(CoverageMapParser.Parse(legacy));

        Assert.StartsWith("{", json, StringComparison.Ordinal);
        var item = Assert.Single(CoverageMapParser.Parse(json));
        Assert.Equal("Mục tiêu / bài toán", item.Label);
        Assert.Equal("RÕ", item.Status);
        Assert.Equal("Quản lý đơn nghỉ phép.", item.Known);
        Assert.Equal("\"app xin nghỉ\"", item.Evidence);
    }

    // ToText dựng lại đúng 12 dòng mà BA đọc trong ngữ cảnh chat (bản đồ lưu JSON, nạp vào prompt dạng
    // bullet — xem BAChatPromptBlocks.CoverageMap). Mất khối {nguồn: …} ở đây là mất bằng chứng khỏi cả
    // ngữ cảnh chat lẫn bản xuất hội thoại.
    [Fact]
    public void ToText_RendersTheBulletFormTheBaReads()
    {
        var text = CoverageMapParser.ToText(CoverageMapParser.Parse(
            "- ★ Mục tiêu / bài toán: [MỘT PHẦN] Quản lý đơn. còn thiếu: ai duyệt {nguồn: \"app xin nghỉ\"}"));

        Assert.Equal(
            "- ★ Mục tiêu / bài toán: [MỘT PHẦN] Quản lý đơn. còn thiếu: ai duyệt {nguồn: \"app xin nghỉ\"}",
            text);
    }

    // Summary là thứ panel tiến độ hiển thị: nó phải ghép lại đúng phần đã ghi nhận + mẩu còn phải hỏi, để
    // đổi format lưu trữ không đổi một pixel nào trên màn hình.
    [Fact]
    public void Summary_JoinsKnownAndGap_ForTheProgressPanel()
    {
        Assert.Equal("Quản lý đơn. còn thiếu: ai duyệt",
            new CoverageMapItem { Known = "Quản lý đơn.", Gap = "ai duyệt" }.Summary);
        Assert.Equal("Quản lý đơn.", new CoverageMapItem { Known = "Quản lý đơn." }.Summary);
        Assert.Equal("còn thiếu: ai duyệt", new CoverageMapItem { Gap = "ai duyệt" }.Summary);
        Assert.Empty(new CoverageMapItem().Summary);
    }
}
