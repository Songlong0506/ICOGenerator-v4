namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Các điểm MÂU THUẪN mà BA phát hiện giữa những điều người dùng đã chốt, soát ngay trước khi soạn tài
/// liệu. Cổng "đủ" (<see cref="Services.Requirements.RequirementReadinessGate"/>) chỉ hỏi *đã rõ hết
/// chưa*; cổng này hỏi *những điều đã rõ có chọi nhau không* — lớp lỗi mà bản đồ bao phủ không thể thấy.
/// </summary>
public class RequirementConflictSet
{
    public List<RequirementConflict> Conflicts { get; set; } = new();
}

/// <summary>
/// Một cặp điều đã chốt không thể cùng đúng, kèm câu hỏi + phương án để người dùng chốt lại bằng một cú
/// bấm. <see cref="SideA"/>/<see cref="SideB"/> phải trích theo lời người dùng để họ nhận ra ngay là mình
/// đã nói cả hai điều — đó là thứ khiến họ tin cổng này thay vì bỏ qua nó.
/// </summary>
public class RequirementConflict
{
    public string Topic { get; set; } = string.Empty;
    public string SideA { get; set; } = string.Empty;
    public string SideB { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
}

/// <summary>Lựa chọn của người dùng cho một mâu thuẫn: câu hỏi + phương án đã chốt (hoặc câu tự nhập).</summary>
public record ConflictResolution(string Question, string Choice);
