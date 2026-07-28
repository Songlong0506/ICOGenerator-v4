using System.Reflection;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Chốt chặn cho phân quyền NGANG (horizontal authorization). [RequirePermission] trả lời "role này có
// quyền X không"; nó KHÔNG chặn được việc user A mở project của user B — việc đó là của
// [RequireProjectAccess] (xem IProjectAccessGuard).
//
// Vì sao cần test này: quên gắn guard không làm gãy gì cả. Trang vẫn chạy, test vẫn xanh, chỉ có điều
// endpoint đó âm thầm mở cho bất kỳ ai đoán/nhặt được GUID (GUID rò qua URL, lịch sử duyệt, log, link
// trong notification). Đây đúng loại lỗi mà review khó bắt vì nó là thứ VẮNG MẶT. Test dưới đây biến
// nó thành lỗi build ngay khi có người thêm action mới nhận projectId mà quên khai báo.
public class ProjectAccessCoverageTests
{
    // Các action tự kiểm tra trong thân hàm vì [RequireProjectAccess] không diễn tả được luật của
    // chúng. Thêm vào đây phải kèm lý do — danh sách này là chỗ duy nhất được phép "biết mà bỏ qua".
    private static readonly Dictionary<string, string> Exempt = new()
    {
        ["AgentDashboardController.DocumentPreview"] =
            "Hai cách định địa chỉ trong cùng một action: có 'path' thì kiểm theo projectId, không thì " +
            "giải ngược theo id của ProjectDocument. Attribute chỉ khai báo được một nguồn id.",
    };

    public static TheoryData<string> ProjectScopedActions()
    {
        var data = new TheoryData<string>();
        foreach (var (controller, action) in AllActions())
        {
            if (IsProjectScoped(action))
                data.Add($"{controller.Name}.{action.Name}");
        }

        return data;
    }

    // Mọi action nhận projectId (trực tiếp hoặc qua view model) PHẢI khai báo [RequireProjectAccess].
    [Theory]
    [MemberData(nameof(ProjectScopedActions))]
    public void ProjectScopedAction_DeclaresAccessGuard(string actionKey)
    {
        if (Exempt.ContainsKey(actionKey))
            return;

        var action = AllActions().Single(x => $"{x.Controller.Name}.{x.Action.Name}" == actionKey).Action;

        Assert.True(
            action.GetCustomAttribute<RequireProjectAccessAttribute>() is not null,
            $"Action '{actionKey}' thao tác theo projectId nhưng thiếu [RequireProjectAccess] — bất kỳ ai " +
            "đoán được GUID cũng gọi được. Gắn attribute, hoặc thêm vào danh sách Exempt kèm lý do nếu " +
            "action tự kiểm tra trong thân hàm.");
    }

    // Bắt lỗi gõ sai tên tham số trong attribute ngay ở CI: ProjectAccessFilter ném lúc CHẠY nếu không
    // tìm thấy tham số, tức là lỗi chỉ lộ ra khi có người bấm đúng chức năng đó.
    [Fact]
    public void EveryGuardAttribute_PointsAtAnExistingParameter()
    {
        foreach (var (controller, action) in AllActions())
        {
            var attribute = action.GetCustomAttribute<RequireProjectAccessAttribute>();
            if (attribute is null)
                continue;

            var path = attribute.IdParameter.Split('.');
            var parameter = action.GetParameters().FirstOrDefault(p => p.Name == path[0]);

            Assert.True(parameter is not null,
                $"[RequireProjectAccess] trên '{controller.Name}.{action.Name}' trỏ tới tham số " +
                $"'{path[0]}' không tồn tại.");

            var type = parameter!.ParameterType;
            for (var i = 1; i < path.Length; i++)
            {
                var property = type.GetProperty(path[i]);
                Assert.True(property is not null,
                    $"[RequireProjectAccess] trên '{controller.Name}.{action.Name}' trỏ tới thuộc tính " +
                    $"'{path[i]}' không có trên '{type.Name}'.");
                type = property!.PropertyType;
            }

            Assert.True(type == typeof(Guid),
                $"[RequireProjectAccess] trên '{controller.Name}.{action.Name}': '{attribute.IdParameter}' " +
                $"phải là Guid, đang là {type.Name}.");
        }
    }

    // Danh sách miễn trừ phải luôn khớp thực tế: xoá/đổi tên action mà quên dọn ở đây thì lần sau người
    // đọc sẽ tin vào một dòng lý do đã hết hiệu lực.
    [Fact]
    public void ExemptionList_HasNoStaleEntries()
    {
        var actual = AllActions()
            .Where(x => IsProjectScoped(x.Action))
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in Exempt.Keys)
        {
            Assert.True(actual.Contains(key),
                $"Danh sách Exempt còn '{key}' nhưng action đó không còn (hoặc không còn nhận projectId).");
        }
    }

    private static bool IsProjectScoped(MethodInfo action) =>
        action.GetParameters().Any(p =>
            (p.Name == "projectId" && p.ParameterType == typeof(Guid))
            || p.ParameterType.GetProperty("ProjectId")?.PropertyType == typeof(Guid));

    private static IEnumerable<(Type Controller, MethodInfo Action)> AllActions()
    {
        var controllers = typeof(RequireProjectAccessAttribute).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Controller).IsAssignableFrom(t));

        foreach (var controller in controllers)
        {
            var actions = controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null);

            foreach (var action in actions)
                yield return (controller, action);
        }
    }
}
