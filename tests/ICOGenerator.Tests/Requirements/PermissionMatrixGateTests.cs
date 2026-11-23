using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng quyết định "đã tới lúc BA soạn bảng phân quyền chưa". Nó là CƠ CHẾ chứ không phải một câu dặn trong
// prompt, vì bản đồ bao phủ ghi nhóm phân quyền là [CHƯA HỎI] suốt cả buổi: prompt có dặn "để cuối" thì
// model vẫn thấy một nhóm chưa hỏi nằm đó và sớm muộn cũng hỏi.
//
// Cái bẫy phải giữ bằng test: RequirementReadinessGate đòi MỌI dòng áp dụng [RÕ] mới mở nút "Write
// Requirement", mà dòng phân quyền chỉ lên [RÕ] khi bảng đã có. Cổng này vì vậy phải BỎ QUA đúng
// dòng phân quyền khi xét — quên một lần là hai cổng khóa lẫn nhau và dự án không bao giờ đi tiếp được.
public class PermissionMatrixGateTests
{
    // Số tác vụ của bản thiết kế UX — các DÒNG của bảng phân quyền.
    private const int Scope = 3;

    private static readonly string EverythingElseClear = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch đào tạo.
        - ★ Đối tượng người dùng & vai trò: [RÕ] HR Assistant, HOD HR, nhân viên.
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo plan, submit theo quý.
        - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] Chưa cần.
        - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
        """);

    [Fact]
    public void ShouldDraft_OpensWhenEveryOtherApplicableGroupIsClear()
    {
        Assert.True(PermissionMatrixGate.ShouldDraft(EverythingElseClear, null, Scope));
    }

    // ĐÂY là test giữ cho hai cổng khỏi khóa lẫn nhau: dòng phân quyền [CHƯA HỎI] ở trên KHÔNG được tính
    // là "buổi phỏng vấn chưa xong".
    [Fact]
    public void ShouldDraft_IgnoresThePermissionRowItself()
    {
        var withPartialPermission = EverythingElseClear
            .Replace(CoverageMapFixture.Map("- Phân quyền theo nghiệp vụ: [CHƯA HỎI]"),
                CoverageMapFixture.Map("- Phân quyền theo nghiệp vụ: [MỘT PHẦN] Còn thiếu: quyền theo màn hình."));

        Assert.True(PermissionMatrixGate.ShouldDraft(withPartialPermission, null, Scope));
    }

    [Fact]
    public void ShouldDraft_StaysClosedWhileAnotherGroupIsStillOpen()
    {
        var stillInterviewing = CoverageMapFixture.With(EverythingElseClear,
            "- Báo cáo / thống kê: [MỘT PHẦN] còn thiếu: báo cáo nào, cho ai xem.");

        Assert.False(PermissionMatrixGate.ShouldDraft(stillInterviewing, null, Scope));
    }

    // Các dòng của bảng LÀ tác vụ của bản thiết kế UX. Chưa thiết kế thì không có gì để soạn.
    [Fact]
    public void ShouldDraft_StaysClosedWithoutAnyTask()
    {
        Assert.False(PermissionMatrixGate.ShouldDraft(EverythingElseClear, null, 0));
    }

    [Fact]
    public void ShouldDraft_StaysClosedOnceTheMatrixIsConfirmed()
    {
        const string confirmed = """
            [{"taskId":"T-01","screen":"Training Plans","function":"Xem","grants":[{"role":"HR Assistant","scope":"của mình"}]}]
            """;

        Assert.True(PermissionMatrixGate.IsConfirmed(confirmed));
        Assert.False(PermissionMatrixGate.ShouldDraft(EverythingElseClear, confirmed, Scope));
    }

    // Bản đồ chưa có / hỏng ⇒ fail-closed, cùng luật với RequirementReadinessGate: distiller giữ con trỏ
    // cũ và gộp bù ở lượt sau nên trạng thái tự lành.
    [Fact]
    public void ShouldDraft_StaysClosedWhenTheCoverageMapIsMissingOrAllNotApplicable()
    {
        Assert.False(PermissionMatrixGate.ShouldDraft(null, null, Scope));
        Assert.False(PermissionMatrixGate.ShouldDraft("không phải bản đồ", null, Scope));
        Assert.False(PermissionMatrixGate.ShouldDraft(CoverageMapFixture.Map("""
            - ★ Mục tiêu / bài toán: [KHÔNG ÁP DỤNG]
            - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
            """), null, Scope));
    }
}
