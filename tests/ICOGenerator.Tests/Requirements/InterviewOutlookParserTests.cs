using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Hai cột "triển vọng phỏng vấn" (Project.OpenQuestions / Project.WorkedExamples) lưu JSON, cùng lý do và
// cùng pattern với bản đồ bao phủ: nhóm của một điểm tồn đọng là ĐẦU VÀO của một chốt chặn tất định
// (CoveragePendingGuard), mà trước đây nó chỉ là một thẻ "[…]" model tự gõ ở đầu chuỗi — gõ chệch khuôn là
// guard câm trong im lặng. Các test dưới chốt bốn thứ mà đổi format phải giữ được.
public class InterviewOutlookParserTests
{
    // Nhóm và câu hỏi là hai TRƯỜNG, đi qua vòng lưu/đọc mà không cần ai bóc chuỗi.
    [Fact]
    public void OpenQuestions_RoundTripThroughStorage()
    {
        var stored = InterviewOutlookParser.SerializeOpenQuestions(OpenQuestionFixture.Items(
            "[Vòng đời & trạng thái] Chưa rõ kết quả Complete dùng để chuyển bước nào",
            "[Quy tắc nghiệp vụ & ràng buộc] Chưa rõ cách tính điểm khi tổng bằng đúng ngưỡng"));

        var items = InterviewOutlookParser.ParseOpenQuestions(stored);

        Assert.Equal(2, items.Count);
        Assert.Equal("Vòng đời & trạng thái", items[0].Group);
        Assert.Equal("Chưa rõ kết quả Complete dùng để chuyển bước nào", items[0].Text);
        Assert.Equal("Quy tắc nghiệp vụ & ràng buộc", items[1].Group);
    }

    // Chữ có dấu KHÔNG được escape thành \uXXXX: hai danh sách này đi vào prompt ở nhiều bước, và bản
    // escape dài gấp ~6 lần. Cùng ràng buộc với CoverageMapParser.
    [Fact]
    public void Storage_KeepsVietnameseCharactersUnescaped()
    {
        var stored = InterviewOutlookParser.SerializeWorkedExamples(new[] { "Tổng điểm 80/90/70 với trọng số 50/30/20 → 81" });

        Assert.NotNull(stored);
        Assert.Contains("Tổng điểm", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkedExamples_RoundTripThroughStorage()
    {
        var stored = InterviewOutlookParser.SerializeWorkedExamples(new[] { "23 người, sĩ số 8–12 ⇒ mở 2 lớp" });

        Assert.Equal(new[] { "23 người, sĩ số 8–12 ⇒ mở 2 lớp" }, InterviewOutlookParser.ParseWorkedExamples(stored));
    }

    [Fact]
    public void EmptyList_IsStoredAsNull()
    {
        Assert.Null(InterviewOutlookParser.SerializeOpenQuestions(Array.Empty<OpenQuestionEntry>()));
        Assert.Null(InterviewOutlookParser.SerializeWorkedExamples(Array.Empty<string>()));
        Assert.Empty(InterviewOutlookParser.ParseOpenQuestions(null));
        Assert.Empty(InterviewOutlookParser.ParseWorkedExamples("   "));
    }

    // Nhãn nhóm là từ vựng NỘI BỘ của bản đồ: prompt chat cấm ném nó vào mặt người dùng nghiệp vụ, nên
    // bản nạp vào ngữ cảnh BA không được mang nó. Đây là phép thử mà CoveragePendingGuard.StripGroupTag
    // từng giữ, nay về đúng chỗ dựng bullet.
    [Fact]
    public void ToText_DropsTheInternalGroupLabel_ToTaggedText_KeepsIt()
    {
        var items = OpenQuestionFixture.Items("[Thông báo / nhắc nhở] Chưa rõ ai nhận email khi ticket chờ duyệt");

        Assert.Equal("- Chưa rõ ai nhận email khi ticket chờ duyệt", InterviewOutlookParser.ToText(items));
        Assert.Equal("- [Thông báo / nhắc nhở] Chưa rõ ai nhận email khi ticket chờ duyệt",
            InterviewOutlookParser.ToTaggedText(items));
    }

    // Mục không có nhóm đi qua nguyên vẹn ở cả hai chiều — không được nuốt mất, và không được sinh ra một
    // cặp ngoặc rỗng "[] …" trong khối echo cho lượt chắt lọc.
    [Fact]
    public void AnItemWithoutAGroup_SurvivesBothRenderings()
    {
        var items = OpenQuestionFixture.Items("Chưa rõ điểm này thuộc nhóm nào");

        Assert.Equal("- Chưa rõ điểm này thuộc nhóm nào", InterviewOutlookParser.ToText(items));
        Assert.Equal("- Chưa rõ điểm này thuộc nhóm nào", InterviewOutlookParser.ToTaggedText(items));
    }

    // TRẦN ĐỘ DÀI phải cắt theo MỤC, không theo ký tự. Format cũ cắt chuỗi ở ký tự thứ 4000 — với bullet
    // thì mất mục cuối, với JSON thì mất SẠCH: một document bị cắt giữa chuỗi không parse lại được, tức
    // trần độ dài tự biến thành một cái bẫy xoá trắng cả danh sách.
    [Fact]
    public void AnOversizedList_IsTrimmedByItem_AndStaysParseable()
    {
        var many = Enumerable.Range(1, 40)
            .Select(i => $"Ví dụ {i}: " + new string('x', 300))
            .ToList();

        var stored = InterviewOutlookParser.SerializeWorkedExamples(many);
        var back = InterviewOutlookParser.ParseWorkedExamples(stored);

        Assert.NotEmpty(back);
        Assert.True(back.Count < many.Count, "danh sách quá dài phải bị bớt mục");
        // Mục nào còn lại thì còn NGUYÊN VẸN, và các mục đầu được giữ (bớt từ cuối).
        Assert.All(back, x => Assert.EndsWith(new string('x', 300), x, StringComparison.Ordinal));
        Assert.StartsWith("Ví dụ 1:", back[0], StringComparison.Ordinal);
    }

    // Một mục đơn lẻ dài quá trần vẫn được giữ: cột là nvarchar(max), còn trả về rỗng ở đây là đúng cái
    // mất-trong-im-lặng mà cả tầng guard này sinh ra để chặn.
    [Fact]
    public void ASingleOversizedItem_IsKeptRatherThanDropped()
    {
        var stored = InterviewOutlookParser.SerializeWorkedExamples(new[] { new string('y', 5000) });

        Assert.Equal(5000, Assert.Single(InterviewOutlookParser.ParseWorkedExamples(stored)).Length);
    }

    // BẢN GHI FORMAT CŨ vẫn đọc được. Khác với bản đồ bao phủ (ghi lại ở MỌI lượt chat, đọc hụt một lần
    // chỉ mất một lượt), hai cột này chỉ được ghi bởi lượt chắt lọc hậu kỳ chat: một dự án đã phỏng vấn
    // xong và đang ở bước sinh AI Design Spec sẽ không có lượt chat nào nữa, nên đọc hụt ở đó là mất VĨNH
    // VIỄN oracle mà POC bị chấm theo.
    [Fact]
    public void LegacyBulletRows_AreStillReadable()
    {
        var legacy = "- [Vòng đời & trạng thái] Chưa rõ kết quả Complete dùng để chuyển bước nào\n"
            + "- Chưa rõ một điểm không gắn thẻ";

        var items = InterviewOutlookParser.ParseOpenQuestions(legacy);

        Assert.Equal(2, items.Count);
        Assert.Equal("Vòng đời & trạng thái", items[0].Group);
        Assert.Equal("Chưa rõ kết quả Complete dùng để chuyển bước nào", items[0].Text);
        Assert.Equal(string.Empty, items[1].Group);
        Assert.Equal("Chưa rõ một điểm không gắn thẻ", items[1].Text);

        Assert.Equal(new[] { "23 người ⇒ mở 2 lớp" },
            InterviewOutlookParser.ParseWorkedExamples("- 23 người ⇒ mở 2 lớp"));
    }

    // Một dòng bullet cũ có chứa dấu ngoặc nhọn vẫn bóc ra được "JSON", và System.Text.Json vui vẻ biến nó
    // thành một document RỖNG — tức nuốt mất nhánh đọc format cũ mà không ai biết.
    [Fact]
    public void ALegacyBulletCarryingBraces_DoesNotGetSwallowedAsEmptyJson()
    {
        var items = InterviewOutlookParser.ParseOpenQuestions("- [Dữ liệu / danh mục chính] Chưa rõ lấy ở đâu");

        Assert.Equal("Dữ liệu / danh mục chính", Assert.Single(items).Group);
    }
}
