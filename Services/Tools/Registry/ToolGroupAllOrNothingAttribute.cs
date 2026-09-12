namespace ICOGenerator.Services.Tools.Registry;

/// <summary>
/// Đánh dấu một class <c>*Tools</c> là nhóm **cấp cả gói**: bật là bật hết, không tích lẻ từng tool.
///
/// <para>
/// Dùng khi các tool trong nhóm chỉ có nghĩa khi đi cùng nhau. Ví dụ <see cref="WebTools"/>: không có
/// <c>OpenUrl</c> thì không có trang nào để bấm, không có <c>Snapshot</c> thì không có số nào để trỏ —
/// nên mười ô tick riêng không phải là mười lựa chọn, nó chỉ là mười chỗ để tích sai. Màn hình Agents
/// gộp cả nhóm thành MỘT dòng, và <c>UpdateAgentUseCase</c> tự nở lựa chọn ra cả nhóm nên chốt nằm ở
/// server chứ không chỉ ở JavaScript.
/// </para>
///
/// <para>
/// Đây KHÔNG phải rào chắn an toàn — nó chỉ bỏ đi một độ chi tiết không có thật. Việc vai nào được cầm
/// nhóm nào vẫn do bảng <c>DbInitializer.DefaultAgents</c> và các test chốt (xem
/// <c>docs/agents-and-tools.md</c>).
/// </para>
/// </summary>
/// <param name="displayName">Nhãn của cả nhóm trên màn hình Agents.</param>
/// <param name="description">Một câu mô tả nhóm làm được gì, hiện cạnh nhãn.</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolGroupAllOrNothingAttribute(string displayName, string description) : Attribute
{
    public string DisplayName { get; } = displayName;
    public string Description { get; } = description;
}

/// <summary>
/// Một nhóm tool cấp-cả-gói, đã phẳng hoá cho tầng Application/View dùng.
/// </summary>
/// <param name="ServiceType">Khoá nhóm — bằng <c>ToolDefinition.ServiceType</c> (tên class).</param>
public sealed record LockedToolGroup(string ServiceType, string DisplayName, string Description);
