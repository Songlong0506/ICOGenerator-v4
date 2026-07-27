namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Một giả định của AI Design Spec bị người dùng bác ở cổng xác nhận giả định, kèm ý đúng họ gõ lại
/// (<see cref="Correction"/> có thể để trống — khi đó chỉ ghi nhận "điều này không đúng").
/// </summary>
public class AssumptionCorrection
{
    public string Assumption { get; set; } = string.Empty;
    public string? Correction { get; set; }
}
