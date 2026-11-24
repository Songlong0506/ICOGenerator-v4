using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng quyết định BA soạn bảng phân quyền LẦN ĐẦU hay chỉ BỔ SUNG dòng. Lượt soạn chạy ở bước "Write
// Requirement" ngay sau lượt thiết kế UX (DesignPreparationService), nên cổng chỉ đọc bản thiết kế và bảng đang
// lưu — KHÔNG đọc bản đồ bao phủ: phân quyền không còn là một nhóm của bản đồ, và một cổng đòi "mọi nhóm khác
// đã [RÕ]" ở đây chỉ lặp lại đúng điều cổng readiness vừa xác nhận.
public class PermissionMatrixGateTests
{
    private const string Confirmed = """
        [{"taskId":"T-01","screen":"Training Plans","function":"Xem","grants":[{"role":"HR Assistant","scope":"của mình"}]}]
        """;

    private static string Blueprint(params (string Id, bool Placed)[] tasks)
        => UxBlueprintBuilder.Serialize(new UxBlueprint
        {
            Tasks = tasks.Select(t => new UxTask { Id = t.Id, Name = "Việc " + t.Id, Placed = t.Placed }).ToList(),
            Screens = { new UxScreen { Id = "S-01", Name = "Training Plans", Actions = tasks.Select(t => new UxAction { Task = t.Id, Label = t.Id }).ToList() } }
        });

    [Fact]
    public void ShouldDraft_OpensOnceTheBlueprintIsDesignedAndFullyPlaced()
    {
        var project = new Project { UxBlueprint = Blueprint(("T-01", true), ("T-02", true)) };

        Assert.True(PermissionMatrixGate.ShouldDraft(project));
    }

    // Các dòng của bảng LÀ tác vụ của bản thiết kế UX. Chưa thiết kế thì không có gì để soạn.
    [Fact]
    public void ShouldDraft_StaysClosedWithoutABlueprint()
    {
        Assert.False(PermissionMatrixGate.ShouldDraft(new Project()));
    }

    // Soạn quyền trên một danh mục còn tác vụ chưa có chỗ là thiếu đúng các tác vụ sắp được xếp.
    [Fact]
    public void ShouldDraft_StaysClosedWhileATaskIsUnplaced()
    {
        var project = new Project { UxBlueprint = Blueprint(("T-01", true), ("T-02", false)) };

        Assert.False(PermissionMatrixGate.ShouldDraft(project));
    }

    [Fact]
    public void ShouldDraft_StaysClosedOnceTheMatrixIsConfirmed()
    {
        var project = new Project { UxBlueprint = Blueprint(("T-01", true)), PermissionMatrix = Confirmed };

        Assert.True(PermissionMatrixGate.IsConfirmed(Confirmed));
        Assert.False(PermissionMatrixGate.ShouldDraft(project));
    }

    // Tác vụ lộ ra SAU lần soạn đầu chỉ được bổ sung dòng — mọi dòng người dùng đã sửa ở popup giữ nguyên.
    [Fact]
    public void MissingTasks_AreTheTasksWithoutARow()
    {
        var project = new Project { UxBlueprint = Blueprint(("T-01", true), ("T-02", true)), PermissionMatrix = Confirmed };

        Assert.Equal("T-02", Assert.Single(PermissionMatrixGate.MissingTasks(project)).Id);
    }
}
