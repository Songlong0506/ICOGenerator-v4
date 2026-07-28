using ICOGenerator.Domain;

namespace ICOGenerator.Data;

// Dữ liệu mẫu đồng bộ từ HR_Portal (bảng OrgUnits) — giữ nguyên Id/giá trị gốc để khớp với dữ liệu
// thật bên HR_Portal. Nội dung nằm ở Data/SeedData/org-units.ndjson (nhúng vào assembly); xem
// AssociatesSeedData để biết vì sao dùng Load() thay cho mảng static.
public static class OrgUnitsSeedData
{
    internal const string ResourceName = "ICOGenerator.Data.SeedData.org-units.ndjson";

    public static OrgUnit[] Load() => SeedDataResource.Load<OrgUnit>(ResourceName);
}
