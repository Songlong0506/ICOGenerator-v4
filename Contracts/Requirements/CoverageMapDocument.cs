using System.ComponentModel;

namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Hình dạng JSON của "Bản đồ bao phủ yêu cầu" — vừa là schema cho structured output của lượt distill
/// (<c>RequirementCoverageService</c>), vừa là format LƯU TRỮ trên <c>Project.RequirementCoverageMap</c>.
/// <para>
/// Tên thuộc tính để ASCII có chủ đích: schema này được gửi thẳng cho model qua
/// <c>response_format: json_schema</c>, và nghĩa của từng trường thì nằm ở <see cref="DescriptionAttribute"/>
/// + prompt, chỗ diễn đạt được đầy đủ hơn một cái tên. Trạng thái vẫn giữ nguyên bốn nhãn tiếng Việt vì
/// chúng là từ vựng nghiệp vụ đã ghim trong prompt, trong DB và trên màn hình.
/// </para>
/// </summary>
public class CoverageMapDocument
{
    [Description("Đúng 12 nhóm thông tin, giữ nguyên thứ tự và tên nhóm của checklist.")]
    public List<CoverageMapEntry> Items { get; set; } = new();
}

/// <summary>Một nhóm thông tin trong <see cref="CoverageMapDocument"/>.</summary>
public class CoverageMapEntry
{
    [Description("Tên nhóm, chép đúng từ checklist.")]
    public string Label { get; set; } = string.Empty;

    [Description("Nhóm cốt lõi (★) hay không.")]
    public bool Core { get; set; }

    [Description("Một trong: RÕ | MỘT PHẦN | CHƯA HỎI | KHÔNG ÁP DỤNG")]
    public string Status { get; set; } = string.Empty;

    [Description("Tóm tắt RẤT NGẮN điều đã biết về nhóm này. Rỗng khi CHƯA HỎI.")]
    public string Known { get; set; } = string.Empty;

    [Description("Điều CÒN PHẢI HỎI để nhóm lên RÕ. Bắt buộc khi MỘT PHẦN; rỗng khi RÕ/CHƯA HỎI/KHÔNG ÁP DỤNG.")]
    public string Gap { get; set; } = string.Empty;

    [Description("Trích NGUYÊN VĂN, ngắn, lời người dùng hoặc câu trong tài liệu nguồn mà kết luận dựa vào. Bắt buộc khi RÕ hoặc MỘT PHẦN. Không diễn đạt lại.")]
    public string Evidence { get; set; } = string.Empty;
}
