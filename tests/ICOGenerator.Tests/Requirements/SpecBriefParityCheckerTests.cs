using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

public class SpecBriefParityCheckerTests
{
    private const string Brief = """
        # App nghỉ phép
        ## Các màn hình chính
        - Danh sách đơn nghỉ: xem các đơn đã gửi
        - Tạo đơn nghỉ — nhập ngày và lý do
        - Màn hình Duyệt đơn: quản lý duyệt/từ chối
        """;

    [Fact]
    public void Check_AllScreensCovered_ReturnsNull()
    {
        var spec = """
            ## 6. Screens To Generate
            ### 6.1. Danh sách đơn nghỉ
            ### 6.2. Tạo đơn nghỉ
            ### 6.3. Duyệt đơn
            ## 10. Business Rules
            - BR-1: x
            """;

        Assert.Null(SpecBriefParityChecker.Check(Brief, spec));
    }

    [Fact]
    public void Check_MissingScreen_ReportsIt()
    {
        var spec = """
            ## 6. Screens To Generate
            ### 6.1. Danh sách đơn nghỉ
            ### 6.2. Tạo đơn nghỉ
            """;

        var report = SpecBriefParityChecker.Check(Brief, spec);

        Assert.NotNull(report);
        Assert.Contains("Duyệt đơn", report);
        Assert.DoesNotContain("Danh sách đơn nghỉ\n- Tạo", report);
    }

    [Fact]
    public void Check_BriefWithoutScreenSection_FailsOpen()
    {
        Assert.Null(SpecBriefParityChecker.Check("# Chỉ có mô tả", "## 6. Screens To Generate\n### 6.1. A"));
    }

    [Fact]
    public void Check_UnparsableSpec_FailsOpen()
    {
        Assert.Null(SpecBriefParityChecker.Check(Brief, "spec tự do không đúng format"));
    }

    [Fact]
    public void ParseBriefScreens_TakesNameBeforeSeparator()
    {
        var screens = SpecBriefParityChecker.ParseBriefScreens(Brief);

        Assert.Equal(3, screens.Count);
        Assert.Equal("Danh sách đơn nghỉ", screens[0]);
        Assert.Equal("Tạo đơn nghỉ", screens[1]);
        Assert.Equal("Màn hình Duyệt đơn", screens[2]);
    }

    // ==== Tầng 2: quy tắc nghiệp vụ (POC phải CHẠY được các rule này) ====

    private const string BriefWithRules = """
        # App nghỉ phép
        ## Các màn hình chính
        - Danh sách đơn nghỉ: xem các đơn đã gửi
        ## Quy tắc cần nhớ
        - Đơn đã duyệt thì không sửa được nữa.
        - Mỗi nhân viên được nghỉ tối đa 12 ngày một năm.
        """;

    [Fact]
    public void Check_RuleMissingFromSpec_ReportsIt()
    {
        var spec = """
            ## 6. Screens To Generate
            ### 6.1. Danh sách đơn nghỉ
            ## 10. Business Rules
            - BR-1: Đơn đã duyệt thì khoá chỉnh sửa, không sửa được nữa
            """;

        var report = SpecBriefParityChecker.Check(BriefWithRules, spec);

        Assert.NotNull(report);
        Assert.Contains("QUY TẮC NGHIỆP VỤ RƠI RỤNG", report);
        Assert.Contains("tối đa 12 ngày", report);
        // Rule đã có trong spec (diễn đạt khác đi) KHÔNG được báo là rơi rụng — nếu không cổng này kêu
        // ở mọi lượt và sẽ bị bỏ qua.
        Assert.DoesNotContain("- Đơn đã duyệt thì không sửa được nữa.", report);
    }

    [Fact]
    public void Check_RuleRephrasedInSpec_CountsAsCovered()
    {
        var spec = """
            ## 6. Screens To Generate
            ### 6.1. Danh sách đơn nghỉ
            ## 10. Business Rules
            - BR-1: Đơn đã duyệt thì khoá chỉnh sửa, không sửa được nữa
            - BR-2: Mỗi nhân viên nghỉ tối đa 12 ngày trong một năm
            """;

        Assert.Null(SpecBriefParityChecker.Check(BriefWithRules, spec));
    }

    [Fact]
    public void ParseBriefRules_KeepsWholeSentence()
    {
        var rules = SpecBriefParityChecker.ParseBriefRules(BriefWithRules);

        Assert.Equal(2, rules.Count);
        // Khác màn hình: cắt ở dấu ':' sẽ mất đúng phần mang nội dung của một quy tắc.
        Assert.Equal("Đơn đã duyệt thì không sửa được nữa.", rules[0]);
    }

    // ==== Tầng 3: câu nghiệm thu (chữ nguyên văn của người dùng, đích của bộ UAT) ====

    private const string BriefWithAcceptance = """
        # App nghỉ phép
        ## Các màn hình chính
        - Danh sách đơn nghỉ: xem các đơn đã gửi
        ## Người dùng làm được những gì?
        - Nhân viên gửi đơn nghỉ phép.
          Hoàn thành khi: gửi xong thì đơn hiện ở danh sách chờ duyệt của quản lý.
        - Quản lý duyệt đơn.
          Hoàn thành khi: duyệt xong thì đơn chuyển sang trạng thái đã duyệt.
        """;

    [Fact]
    public void Check_AcceptanceCriterionMissingFromSpec_ReportsIt()
    {
        var spec = """
            ## 6. Screens To Generate
            ### 6.1. Danh sách đơn nghỉ
            ## 14. Acceptance Criteria
            - AC-1 (Nhân viên gửi đơn nghỉ phép): gửi xong thì đơn hiện ở danh sách chờ duyệt của quản lý.
            """;

        var report = SpecBriefParityChecker.Check(BriefWithAcceptance, spec);

        Assert.NotNull(report);
        Assert.Contains("CÂU NGHIỆM THU RƠI RỤNG", report);
        Assert.Contains("duyệt xong thì đơn chuyển sang trạng thái đã duyệt", report);
    }

    [Fact]
    public void Check_AllAcceptanceCriteriaCopied_ReturnsNull()
    {
        var spec = """
            ## 6. Screens To Generate
            ### 6.1. Danh sách đơn nghỉ
            ## 14. Acceptance Criteria
            - AC-1 (Nhân viên gửi đơn nghỉ phép): gửi xong thì đơn hiện ở danh sách chờ duyệt của quản lý.
            - AC-2 (Quản lý duyệt đơn): duyệt xong thì đơn chuyển sang trạng thái đã duyệt.
            """;

        Assert.Null(SpecBriefParityChecker.Check(BriefWithAcceptance, spec));
    }

    // Spec bỏ HẲN mục § 14 là trường hợp đắt nhất — không cổng nào phía sau còn kiểm được câu nghiệm thu,
    // nên nó phải bị báo chứ không được rơi vào nhánh fail-open.
    [Fact]
    public void Check_SpecWithoutAcceptanceSection_ReportsEveryCriterion()
    {
        var spec = """
            ## 6. Screens To Generate
            ### 6.1. Danh sách đơn nghỉ
            ## 10. Business Rules
            - BR-1: x
            """;

        var report = SpecBriefParityChecker.Check(BriefWithAcceptance, spec);

        Assert.NotNull(report);
        Assert.Contains("AC-1", report);
        Assert.Contains("AC-2", report);
    }
}
