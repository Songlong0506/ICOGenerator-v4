using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Tests.Requirements;

/// <summary>
/// Lời đáp tối thiểu cho các lời gọi THIẾT KẾ mà bước "Write Requirement" chạy trước khi soạn Brief
/// (<see cref="DesignPreparationService"/>): một tác vụ, một màn hình đặt nó, không bảng phân quyền nào. Dùng cho
/// các test của vòng soạn Brief — chúng kiểm phần Brief, không kiểm thiết kế (xem
/// <c>DesignPreparationServiceTests</c>), nhưng vòng soạn dừng hẳn khi không dựng được bản thiết kế.
/// </summary>
internal static class DesignCallFixture
{
    /// <summary>
    /// Lời đáp cho <paramref name="purpose"/> nếu đó là một lời gọi thiết kế; <c>false</c> khi không phải.
    /// </summary>
    public static bool TryAnswer(string purpose, out object? value)
    {
        switch (purpose)
        {
            case "BAUxInventory":
                value = new UxInventoryDraft
                {
                    Objects = { new UxObject { Name = "Request" } },
                    Tasks = { new UxTaskDraft { Name = "Gửi yêu cầu", Object = "Request", Kind = "create", Actors = { "Nhân viên" } } }
                };
                return true;
            case "BAUxInventoryOrphans":
                value = new UxInventoryDraft();
                return true;
            case "UiUxBlueprint":
            case "UiUxBlueprintRevision":
                value = new UxBlueprintDraft
                {
                    Screens =
                    {
                        new UxScreenDraft
                        {
                            Name = "Requests", Pattern = "list", Object = "Request", Audience = { "Nhân viên" },
                            Actions = { new UxAction { Task = "T-01", Label = "Gửi yêu cầu", Placement = "toolbar", Prominence = "primary" } }
                        }
                    },
                    Navigation =
                    {
                        new UxNavigation { Role = "Nhân viên", Landing = "Requests", Items = { new UxNavItem { Label = "Requests", Screen = "Requests" } } }
                    }
                };
                return true;
            case "BAPermissionMatrixDraft":
            case "BAPermissionMatrixPatch":
                value = new PermissionMatrixDraft();
                return true;
            default:
                value = null;
                return false;
        }
    }

    public static DesignPreparationService NewService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
        => new(new ReportMapDraftService(db, llm, prompts), new UxDesignService(db, llm, prompts), new PermissionMatrixDraftService(db, llm, prompts));
}
