using ICOGenerator.Application.Audit;
using ICOGenerator.Application.Shared;
using Xunit;

namespace ICOGenerator.Tests.Shared;

// Ô "Elements per page" phải luôn hiện đúng số dòng mỗi trang đang trả về. Khi pageSize không nằm
// trong bộ lựa chọn, không option nào được chọn và trình duyệt hiện option đầu (10) — lỗi đã gặp ở
// Audit Log (mặc định 20).
public class PagerModelTests
{
    private static PagerModel Pager(int pageSize) => new()
    {
        Page = 1,
        TotalPages = 3,
        PageSize = pageSize,
        PageUrl = p => $"?page={p}",
        PageSizeUrl = s => $"?pageSize={s}"
    };

    [Fact]
    public void SizeOptions_PageSizeTrongDanhSach_GiuNguyen()
        => Assert.Equal(new[] { 10, 50, 100 }, Pager(50).SizeOptions());

    [Fact]
    public void SizeOptions_PageSizeNgoaiDanhSach_ChenVaoDungThuTu()
        => Assert.Equal(new[] { 10, 20, 50, 100 }, Pager(20).SizeOptions());

    [Fact]
    public void AuditLog_MacDinhNamTrongBoLuaChon()
        => Assert.Contains(GetAuditLogPageQuery.DefaultPageSize, Pager(0).PageSizes);
}
