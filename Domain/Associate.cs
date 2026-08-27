using System.ComponentModel.DataAnnotations;

namespace ICOGenerator.Domain;

/// <summary>
/// Một nhân sự trong bản sao dữ liệu tổ chức đồng bộ từ HR_Portal (seed ở
/// <see cref="Data.AssociatesSeedData"/>). Bảng này CHỈ phục vụ hai đường đọc:
/// gợi ý người nhận bản demo (<c>SearchAssociatesQuery</c>) và số liệu GỘP cho bối cảnh tổ chức của
/// prompt BA (<c>OrganizationContextService</c>).
///
/// <para>
/// Vì vậy ở đây chỉ giữ đúng những cột hai đường đọc đó dùng tới. Các cột HR còn lại (ngày sinh, giới
/// tính, điện thoại, địa chỉ đón, cost center, ngày vào làm, dấu vết created/updated...) đã bỏ: không
/// một dòng code nào đọc, mà vẫn kéo theo hồ sơ cá nhân vào DB lẫn file seed trong repo. Cần thêm cột
/// nào thì thêm cùng lúc với đường đọc dùng nó, và bổ sung khoá tương ứng vào
/// <c>Data/SeedData/associates.ndjson</c>.
/// </para>
/// </summary>
public class Associate
{
    public Guid Id { get; set; }

    /// <summary>Xoá mềm từ HR_Portal — mọi truy vấn đều lọc <c>!IsDelete</c>.</summary>
    public bool IsDelete { get; set; }

    /// <summary>Mã nhân sự; là thứ <c>OrgUnit.TrgtManagerLId</c> trỏ tới khi tra tên quản lý.</summary>
    [MaxLength(50)]
    public string? PersonalNumber { get; set; }

    [MaxLength(255)]
    public string? DisplayName { get; set; }

    /// <summary>Mã đơn vị — khoá tra cứu sang <see cref="OrgUnit.OrgUnitCode"/> (đếm headcount).</summary>
    [MaxLength(50)]
    public string? OrgUnitCode { get; set; }

    /// <summary>Tên đơn vị dạng chuỗi (vd "HcP/TEF3.3"), hiển thị kèm gợi ý người nhận.</summary>
    [MaxLength(255)]
    public string? OrganizationUnit { get; set; }

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(255)]
    public string? Position { get; set; }

    /// <summary>Tài khoản NT (vd "LHN9HC") — một trong các khoá tìm kiếm của ô "Gửi cho ai".</summary>
    [MaxLength(100)]
    public string? UserId { get; set; }

    /// <summary>Ngày nghỉ việc; có giá trị trong quá khứ ⇒ loại khỏi mọi danh sách.</summary>
    public DateTime? LeavingDate { get; set; }
}
