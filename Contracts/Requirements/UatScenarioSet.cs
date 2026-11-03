namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Bộ kịch bản UAT sinh từ AI Design Spec sau khi POC dựng xong — hiển thị thành checklist từng bước
/// ở trang POC Review để người dùng thường review có kịch bản thay vì khám phá tự do.
/// Lưu tại <c>04_Implementation/uat-scenarios.json</c> trong workspace của project.
/// </summary>
public class UatScenarioSet
{
    public List<UatScenario> Scenarios { get; set; } = new();
}

public class UatScenario
{
    public string Title { get; set; } = string.Empty;

    /// <summary>Màn hình chính của kịch bản (nguyên văn tên trong spec) — dùng gắn ghi chú khi báo lỗi.</summary>
    public string Screen { get; set; } = string.Empty;

    /// <summary>Mã Business Rule (BR-n) kịch bản này kiểm chứng; rỗng nếu không gắn rule nào.</summary>
    public List<string> RuleRefs { get; set; } = new();

    public List<string> Steps { get; set; } = new();
}
