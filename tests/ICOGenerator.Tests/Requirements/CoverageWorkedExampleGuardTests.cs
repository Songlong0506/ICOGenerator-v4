using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// QUY TẮC CHỞ CON SỐ mà chưa có ví dụ tính thử nào thì không được [RÕ].
//
// Công thức hiểu sai là lỗi mà không cổng nào phía sau bắt được: các cổng đều hỏi "có thông tin chưa",
// không hỏi "thông tin đó có đúng không". Tài liệu sẽ ghi đúng… điều đã hiểu sai, rồi spec và POC sai
// theo. Thứ duy nhất bắt được là một ví dụ số người dùng đã xác nhận — nó vừa là bằng chứng hiểu đúng,
// vừa là oracle mà bản demo bị chấm theo (PocWorkedExampleOracle).
//
// Ca thật (dự án JD Libary 5, lượt 13): người dùng nêu "Responsibility (5 cái và có %, và có 1 item mặc
// định không được sửa là «Other task assign by manager» % từ 5-10)". BA ghi nhận nguyên văn rồi đi tiếp;
// dòng «Quy tắc nghiệp vụ & ràng buộc» chở đủ các con số ấy, mục "Ví dụ đã xác nhận" trống trơn suốt
// buổi. Ba câu không ai trả lời: 5 là cố định hay tối thiểu, tổng % có bằng 100 không, và khoảng 5–10 là
// của riêng dòng mặc định hay của mọi dòng.
public class CoverageWorkedExampleGuardTests
{
    // Bản đồ lưu dạng JSON ⇒ các test soi TRƯỜNG đã parse thay vì chuỗi: trạng thái, phần đã ghi nhận,
    // mẩu còn phải hỏi và bằng chứng là thứ những tầng sau đọc; cách xếp chữ thì không tầng nào dựa vào.
    private static ICOGenerator.Contracts.Requirements.CoverageMapItem Row(string? map, string labelPrefix) =>
        CoverageMapParser.Parse(map).First(x => x.Label.StartsWith(labelPrefix, StringComparison.Ordinal));

    private static readonly string RuleRowWithNumbers =
        CoverageMapFixture.Map("- Quy tắc nghiệp vụ & ràng buộc: [RÕ] Responsibility có 5 cái kèm %, 1 item mặc định % từ 5-10. "
        + "{nguồn: \"Responsibility( 5 cái và có %)\"}");

    [Fact]
    public void ARuleRowCarryingNumbers_IsDowngraded_WhenNoWorkedExampleIsConfirmed()
    {
        var map = CoverageWorkedExampleGuard.Apply(RuleRowWithNumbers, workedExamples: Array.Empty<string>());

        Assert.NotNull(map);
        var row = Row(map, "Quy tắc nghiệp vụ");
        Assert.Equal("MỘT PHẦN", row.Status);
        Assert.Equal(CoverageWorkedExampleGuard.MissingExampleQuestion, row.NextQuestion);

        // Bằng chứng của dòng phải sống sót: nó là thứ panel tiến độ hiển thị và là chỗ distiller bám vào.
        Assert.Equal("\"Responsibility( 5 cái và có %)\"", row.Evidence);
    }

    [Fact]
    public void AConfirmedWorkedExample_OpensTheRowBackUp()
    {
        var map = CoverageWorkedExampleGuard.Apply(
            RuleRowWithNumbers,
            new[] { "5 Responsibility với % 30/25/20/15/10 thì tổng bằng 100% → hợp lệ" });

        Assert.Equal(RuleRowWithNumbers, map);
    }

    // Ranh giới 1: con số ở nhóm KHÁC không phải công thức. Số người dùng, số trường dữ liệu, số màn hình
    // không cần oracle nào — soi cả bản đồ là biến guard này thành một cái cổng đóng thường trực.
    [Fact]
    public void NumbersInOtherGroups_AreLeftAlone()
    {
        var map = CoverageMapFixture.Map("""
            - Quy mô sử dụng: [RÕ] Khoảng 1549 nhân sự toàn nhà máy dùng ứng dụng.
            - Dữ liệu / danh mục chính: [RÕ] Một JD gồm 9 thông tin: mã JD, OrgUnit, JobTitle…
            """);

        Assert.Equal(map, CoverageWorkedExampleGuard.Apply(map, workedExamples: Array.Empty<string>()));
    }

    // Ranh giới 2: quy tắc thuần định tính (không con số) đã đủ khi nêu được điều kiện và hệ quả — không
    // có gì để tính thử, nên đòi ví dụ số ở đây là dựng một câu hỏi không có câu trả lời.
    [Fact]
    public void AQualitativeRuleRow_IsLeftAlone()
    {
        var map =
            CoverageMapFixture.Map("- Quy tắc nghiệp vụ & ràng buộc: [RÕ] JD phải qua HRBP verify rồi HoD approve mới available để assign.");

        Assert.Equal(map, CoverageWorkedExampleGuard.Apply(map, workedExamples: Array.Empty<string>()));
    }

    // Ranh giới 3: dòng đã có mẩu hỏi riêng của distiller thì để nguyên — mẩu đó bám vào đúng quy tắc còn
    // hụt nên cụ thể hơn mẩu dựng sẵn, và chồng hai mẩu lên nhau thì cổng phát ra một câu hỏi kép.
    [Fact]
    public void ARowThatAlreadyCarriesItsOwnGap_IsNotOverwritten()
    {
        var map =
            CoverageMapFixture.Map("- Quy tắc nghiệp vụ & ràng buộc: [MỘT PHẦN] Responsibility có 5 cái kèm %. "
            + "còn thiếu: tổng % của các Responsibility phải bằng bao nhiêu");

        Assert.Equal(map, CoverageWorkedExampleGuard.Apply(map, workedExamples: Array.Empty<string>()));
    }

    // Guard chỉ HẠ, không bao giờ nâng: một dòng [KHÔNG ÁP DỤNG] hay [CHƯA HỎI] không bị đụng tới.
    [Fact]
    public void RowsThatAreNotClearOrPartial_AreUntouched()
    {
        var map = CoverageMapFixture.Map("""
            - Quy tắc nghiệp vụ & ràng buộc: [CHƯA HỎI]
            - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] người dùng nói không cần báo cáo nào.
            """);

        Assert.Equal(map, CoverageWorkedExampleGuard.Apply(map, workedExamples: Array.Empty<string>()));
    }
}
