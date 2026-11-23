namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Đầu ra của lượt DANH MỤC TÁC VỤ (<c>Prompts/BusinessAnalyst/ux-inventory.v1.md</c>): các đối tượng ứng dụng
/// quản lý, các tác vụ người dùng làm trên chúng, và các bước luồng diễn ra NGOÀI phần mềm.
///
/// <para>
/// Không đi thẳng vào DB: <c>UxInventoryBuilder.Build</c> chuẩn hoá tên, cấp ID ổn định (giữ ID cũ cho tác vụ
/// đã có), gieo tác vụ báo cáo từ bảng báo cáo một cách TẤT ĐỊNH, và đối chiếu từng bước luồng.
/// </para>
/// </summary>
public class UxInventoryDraft
{
    public List<UxObject> Objects { get; set; } = new();

    public List<UxTaskDraft> Tasks { get; set; } = new();

    /// <summary>Bước luồng con người làm bên ngoài ứng dụng (gọi điện, bộ phận khác gửi email…).</summary>
    public List<UxOutsideStep> OutsideSoftware { get; set; } = new();
}

/// <summary>Một tác vụ model đề xuất — <see cref="UxTask"/> trừ hai trường do code giữ (ID, cờ xếp chỗ).</summary>
public class UxTaskDraft
{
    public string Name { get; set; } = "";
    public string Object { get; set; } = "";
    public string Kind { get; set; } = UxTaskKind.Browse;
    public List<string> Actors { get; set; } = new();
    public List<string> FlowSteps { get; set; } = new();
    public string FromState { get; set; } = "";
    public string ToState { get; set; } = "";
    public string Source { get; set; } = UxTaskSource.Conversation;
}

/// <summary>Một bước luồng BA khai là làm ngoài phần mềm, kèm lý do theo lời nghiệp vụ.</summary>
public class UxOutsideStep
{
    /// <summary>Phần <c>action</c> của bước, chép đúng chữ bảng luồng.</summary>
    public string Step { get; set; } = "";

    public string Reason { get; set; } = "";
}

/// <summary>
/// Đầu ra của lượt THIẾT KẾ (<c>Prompts/UiUx/ux-blueprint.v1.md</c>): các màn hình và điều hướng theo vai.
/// Chuẩn hoá bởi <c>UxBlueprintBuilder.FromDraft</c> — hành động trỏ vào tác vụ không có thật bị bỏ, tên
/// màn trùng bị gộp, ID màn do code cấp.
/// </summary>
public class UxBlueprintDraft
{
    public List<UxScreenDraft> Screens { get; set; } = new();

    public List<UxNavigation> Navigation { get; set; } = new();
}

/// <summary>Một màn hình model đề xuất — <see cref="UxScreen"/> trừ ID.</summary>
public class UxScreenDraft
{
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = UxPattern.List;
    public string Object { get; set; } = "";
    public string Purpose { get; set; } = "";
    public List<string> Audience { get; set; } = new();
    public string Entry { get; set; } = UxEntry.Nav;
    public List<string> Tabs { get; set; } = new();
    public List<UxAction> Actions { get; set; } = new();
    public string Rationale { get; set; } = "";
}

/// <summary>
/// ĐẦU VÀO máy đọc được của lượt thiết kế (<c>UiUx/ux-blueprint.v1.md</c>) — render thành một khối JSON trong
/// prompt. JSON chứ không văn xuôi vì hai lẽ: model chép ID tác vụ chính xác hơn hẳn khi ID đứng trong một
/// cấu trúc, và Prompt Evals đọc NGƯỢC được chính khối này để chấm tất định đầu ra của model
/// (<c>UxBlueprintEvalScorer</c>) — golden set và production dùng đúng một định dạng.
/// </summary>
public class UxDesignInput
{
    /// <summary>"design" (thiết kế tự do) | "patch" (giữ cấu trúc, chỉ xếp chỗ phần mới) | "feedback" (sửa theo góp ý).</summary>
    public string Mode { get; set; } = UxDesignMode.Design;

    public List<string> Roles { get; set; } = new();
    public List<UxObject> Objects { get; set; } = new();
    public List<UxDesignInputTask> Tasks { get; set; } = new();
    public List<UxDesignInputFlow> Flows { get; set; } = new();
    public List<UxDesignInputReport> Reports { get; set; } = new();

    /// <summary>Phạm vi dữ liệu theo vai của từng tác vụ (khi đã có bảng phân quyền): "T-03: FCM (tất cả), Nhân viên (của mình)".</summary>
    public List<string> DataScopes { get; set; } = new();

    /// <summary>Bản thiết kế đang có (chế độ patch/feedback, hoặc thiết kế lại) — để giữ ổn định thứ không cần đổi.</summary>
    public List<UxScreenDraft> CurrentScreens { get; set; } = new();
    public List<UxNavigation> CurrentNavigation { get; set; } = new();

    /// <summary>Góp ý thiết kế người dùng đã gửi — phải được tôn trọng ở mọi lần thiết kế.</summary>
    public List<string> DesignNotes { get; set; } = new();
}

public static class UxDesignMode
{
    public const string Design = "design";
    public const string Patch = "patch";
    public const string Feedback = "feedback";
}

public class UxDesignInputTask
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Object { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<string> Actors { get; set; } = new();
    public string FromState { get; set; } = "";
    public string ToState { get; set; } = "";
    public bool Placed { get; set; }
}

public class UxDesignInputFlow
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<UxDesignInputStep> Steps { get; set; } = new();
}

public class UxDesignInputStep
{
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";

    /// <summary>ID tác vụ phụ trách bước (rỗng khi bước làm ngoài phần mềm).</summary>
    public string Task { get; set; } = "";

    public bool Outside { get; set; }
}

public class UxDesignInputReport
{
    public string Task { get; set; } = "";
    public string Report { get; set; } = "";

    /// <summary>"dashboard" (một khối của màn tổng quan) | "counter" (ô đếm trên màn tổng quan, bấm mở danh sách đã lọc) | "own-screen".</summary>
    public string Placement { get; set; } = "";

    /// <summary>Tên màn hình bảng báo cáo đã chốt cho báo cáo này.</summary>
    public string Screen { get; set; } = "";

    public string Breakdown { get; set; } = "";
}
