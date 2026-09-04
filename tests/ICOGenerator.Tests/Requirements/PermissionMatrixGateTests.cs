using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng quyết định "đã tới lúc bày bảng phân quyền chưa". Nó là CƠ CHẾ chứ không phải một câu dặn trong
// prompt, vì bản đồ bao phủ ghi nhóm phân quyền là [CHƯA HỎI] suốt cả buổi: prompt có dặn "để cuối" thì
// model vẫn thấy một nhóm chưa hỏi nằm đó và sớm muộn cũng hỏi.
//
// Cái bẫy phải giữ bằng test: RequirementReadinessGate đòi MỌI dòng áp dụng [RÕ] mới mở nút "Write
// Requirement", mà dòng phân quyền chỉ lên [RÕ] sau khi bảng được chốt. Cổng này vì vậy phải BỎ QUA đúng
// dòng phân quyền khi xét — quên một lần là hai cổng khóa lẫn nhau và dự án không bao giờ đi tiếp được.
public class PermissionMatrixGateTests
{
    private static readonly List<string> Scope = new() { "Màn hình Training Plan" };

    private static readonly string EverythingElseClear = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch đào tạo.
        - ★ Đối tượng người dùng & vai trò: [RÕ] HR Assistant, HOD HR, nhân viên.
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo plan, submit theo quý.
        - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] Chưa cần.
        - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
        """);

    [Fact]
    public void ShouldAsk_OpensWhenEveryOtherApplicableGroupIsClear()
    {
        Assert.True(PermissionMatrixGate.ShouldAsk(EverythingElseClear, null, Scope));
    }

    // ĐÂY là test giữ cho hai cổng khỏi khóa lẫn nhau: dòng phân quyền [CHƯA HỎI] ở trên KHÔNG được tính
    // là "buổi phỏng vấn chưa xong".
    [Fact]
    public void ShouldAsk_IgnoresThePermissionRowItself()
    {
        var withPartialPermission = EverythingElseClear
            .Replace(CoverageMapFixture.Map("- Phân quyền theo nghiệp vụ: [CHƯA HỎI]"),
                CoverageMapFixture.Map("- Phân quyền theo nghiệp vụ: [MỘT PHẦN] Còn thiếu: quyền theo màn hình."));

        Assert.True(PermissionMatrixGate.ShouldAsk(withPartialPermission, null, Scope));
    }

    [Fact]
    public void ShouldAsk_StaysClosedWhileAnotherGroupIsStillOpen()
    {
        var stillInterviewing = CoverageMapFixture.With(EverythingElseClear,
            "- Báo cáo / thống kê: [MỘT PHẦN] còn thiếu: báo cáo nào, cho ai xem.");

        Assert.False(PermissionMatrixGate.ShouldAsk(stillInterviewing, null, Scope));
    }

    // Các dòng của bảng LÀ màn hình đã chắt từ hội thoại. Phạm vi trống thì không có gì để hỏi — và một
    // bảng dựng trên phạm vi mới có một nửa còn tệ hơn không có bảng, vì quyền của các màn hình còn lại
    // sẽ vĩnh viễn không ai hỏi tới.
    [Fact]
    public void ShouldAsk_StaysClosedWithoutAPlannedScope()
    {
        Assert.False(PermissionMatrixGate.ShouldAsk(EverythingElseClear, null, new List<string>()));
    }

    [Fact]
    public void ShouldAsk_StaysClosedOnceTheMatrixIsConfirmed()
    {
        const string confirmed = """
            [{"screen":"Màn hình Training Plan","function":"Xem","grants":[{"role":"HR Assistant","scope":"của mình"}]}]
            """;

        Assert.True(PermissionMatrixGate.IsConfirmed(confirmed));
        Assert.False(PermissionMatrixGate.ShouldAsk(EverythingElseClear, confirmed, Scope));
    }

    // Bản đồ chưa có / hỏng ⇒ fail-closed, cùng luật với RequirementReadinessGate: distiller giữ con trỏ
    // cũ và gộp bù ở lượt sau nên trạng thái tự lành.
    [Fact]
    public void ShouldAsk_StaysClosedWhenTheCoverageMapIsMissingOrAllNotApplicable()
    {
        Assert.False(PermissionMatrixGate.ShouldAsk(null, null, Scope));
        Assert.False(PermissionMatrixGate.ShouldAsk("không phải bản đồ", null, Scope));
        Assert.False(PermissionMatrixGate.ShouldAsk(CoverageMapFixture.Map("""
            - ★ Mục tiêu / bài toán: [KHÔNG ÁP DỤNG]
            - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
            """), null, Scope));
    }
}
