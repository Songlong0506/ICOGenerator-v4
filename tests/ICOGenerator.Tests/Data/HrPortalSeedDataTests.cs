using ICOGenerator.Data;
using Xunit;

namespace ICOGenerator.Tests.Data;

// Chốt chặn cho bộ seed HR_Portal sau khi chuyển từ mảng C# sang JSON nhúng (Data/SeedData/*.json).
// Rủi ro mới của dạng nhúng là hỏng ÂM THẦM: quên khai báo <EmbeddedResource>, sai LogicalName, hay
// file JSON bị cắt/sửa hỏng — app vẫn build, chỉ tới lúc khởi động mới ném lỗi (hoặc tệ hơn: seed
// thiếu bản ghi). Các test dưới đây bắt cả ba trường hợp đó ngay ở CI.
public class HrPortalSeedDataTests
{
    // Số bản ghi tại thời điểm chuyển đổi từ mảng C# sang JSON. Chuyển đổi này được verify round-trip
    // (serialize mảng gốc -> JSON -> deserialize -> so khớp) nên hai con số này là ảnh chụp dữ liệu gốc.
    // Chỉ sửa khi CHỦ ĐỘNG đồng bộ lại dữ liệu từ HR_Portal.
    private const int ExpectedAssociateCount = 1549;
    private const int ExpectedOrgUnitCount = 195;

    [Fact]
    public void Load_ReadsEveryAssociateFromEmbeddedJson()
    {
        var associates = AssociatesSeedData.Load();

        Assert.Equal(ExpectedAssociateCount, associates.Length);
        Assert.All(associates, x => Assert.NotEqual(Guid.Empty, x.Id));
        Assert.Equal(
            associates.Length,
            associates.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void Load_ReadsEveryOrgUnitFromEmbeddedJson()
    {
        var orgUnits = OrgUnitsSeedData.Load();

        Assert.Equal(ExpectedOrgUnitCount, orgUnits.Length);
        Assert.All(orgUnits, x => Assert.NotEqual(Guid.Empty, x.Id));
        Assert.Equal(
            orgUnits.Length,
            orgUnits.Select(x => x.Id).Distinct().Count());
    }

    // Một bản ghi mốc, đủ mọi KIỂU dữ liệu có trong entity: Guid, DateTime có phần lẻ giây, DateTime
    // chỉ có ngày, string, bool. Nếu tuỳ chọn JsonSerializer lúc đọc lệch khỏi lúc ghi (vd đổi
    // DefaultIgnoreCondition) thì sai lệch lộ ra ở đây chứ không âm thầm trôi vào DB.
    [Fact]
    public void Load_PreservesFieldValuesExactly()
    {
        var associate = Assert.Single(
            AssociatesSeedData.Load(),
            x => x.Id == Guid.Parse("50CCF4D7-3915-4F74-8DF0-00A1939CD65C"));

        Assert.Equal("35962752", associate.PersonalNumber);
        Assert.Equal("11954888", associate.GlobalId);
        Assert.Equal("Le Anh Hao", associate.DisplayName);
        Assert.Equal("PS/EPC2-VN", associate.OrganizationUnit);
        Assert.Equal("HAO.LEANH@VN.BOSCH.COM", associate.Email);
        Assert.Equal("Technical Documentation Engineer", associate.Position);
        Assert.Equal("LHN9HC", associate.UserId);
        Assert.Equal(DateTime.Parse("2025-03-19 12:00:00.6655978"), associate.CreatedDate);
        Assert.Equal(DateTime.Parse("2025-03-12 00:00:00.0000000"), associate.HiredDate);
        // Các trường vắng mặt trong JSON phải trở về đúng giá trị mặc định của property.
        Assert.False(associate.IsDelete);
        Assert.False(associate.IsIndirect);
        Assert.Null(associate.Birthday);
        Assert.Null(associate.LeavingDate);
    }

    [Fact]
    public void Load_PreservesOrgUnitFieldValuesExactly()
    {
        var orgUnit = Assert.Single(
            OrgUnitsSeedData.Load(),
            x => x.Id == Guid.Parse("8BA9C19B-B26A-4976-B60D-02EA83BDCE68"));

        Assert.Equal("HcP/MFE2.12", orgUnit.DisplayName);
        Assert.Equal("00001835050010", orgUnit.CostCenter);
        Assert.Equal("993F9106-79CB-4BB0-B061-EBDE116C32B6", orgUnit.UpdatedBy);
        Assert.Equal(DateTime.Parse("2023-12-05 11:15:09.3212666"), orgUnit.CreatedDate);
    }
}
