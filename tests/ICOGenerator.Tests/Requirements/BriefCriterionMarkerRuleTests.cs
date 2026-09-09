using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cụm "Hoàn thành khi:" là một GIAO ƯỚC giữa ba prompt và một parser, và không có mối nối nào compiler
// kiểm được.
//
// Đường đi của nó: product-brief.v3.md bắt model viết một dòng "Hoàn thành khi: …" dưới MỖI tính năng
// chính → BriefAcceptanceCriteria bóc các dòng ấy khỏi Brief đã duyệt → ai-design-spec.v1.md lấy chính
// chúng làm nguồn DUY NHẤT cho "## 14. Acceptance Criteria" → UatScenarioService sinh kịch bản nghiệm thu
// người dùng sắp bấm thử, còn SpecBriefParityChecker soát câu nào rơi rụng dọc đường.
//
// Một lần dọn prompt viết lại cụm cho "mượt hơn" ("Xong khi:", "Đạt khi:") là đứt cả chuỗi đó — mà triệu
// chứng hoàn toàn im lặng: parser không tìm thấy dòng nào, § 14 rỗng, UAT quay về suy diễn từ bản kỹ
// thuật, và không có lỗi nào nổ ra. Test này bắt đúng cú đứt ấy ở CI.
public class BriefCriterionMarkerRuleTests
{
    // Ba vai của cụm: nơi BẮT viết, nơi SOÁT thiếu, nơi ĐỌC lại.
    [Theory]
    [InlineData("BusinessAnalyst/product-brief.v3.md")]
    [InlineData("BusinessAnalyst/product-brief-review.v2.md")]
    [InlineData("BusinessAnalyst/ai-design-spec.v1.md")]
    public void PromptsUseTheExactMarkerTheParserLooksFor(string promptKey)
    {
        var prompt = PromptFixture.Read(promptKey);

        Assert.Contains(BriefAcceptanceCriteria.CriterionMarker, prompt, StringComparison.Ordinal);
    }

    // Không đủ nếu prompt chỉ NHẮC tới cụm: nó phải dạy đúng hình dạng mà parser bóc được — cụm, dấu hai
    // chấm, rồi nội dung. Dựng lại đúng một dòng như prompt mô tả và đòi parser đọc ra nó.
    [Fact]
    public void ParserReadsALineWrittenTheWayThePromptTeaches()
    {
        var brief = "- Quản lý kế hoạch đào tạo\n"
                    + $"  {BriefAcceptanceCriteria.CriterionMarker}: nhân viên gửi đơn xong thì quản lý nhìn thấy đơn đó.\n";

        var criteria = BriefAcceptanceCriteria.Parse(brief);

        var one = Assert.Single(criteria);
        Assert.Equal("Quản lý kế hoạch đào tạo", one.Feature);
        Assert.Equal("nhân viên gửi đơn xong thì quản lý nhìn thấy đơn đó.", one.Criterion);
    }
}
