namespace ICOGenerator.Application.Shared;

/// <summary>
/// View model cho pager phía server dùng chung (mẫu Bảng chuẩn Bosch), render qua partial
/// <c>Shared/_Pager</c>. Dùng cho các màn hình phân trang phía server (Models, Audit, Projects):
/// một ô chọn "Elements per page" bên trái và dải số trang bên phải (chevron ‹ ›, nút tròn,
/// trang hiện tại là chấm tròn xanh Bosch). Trông giống hệt pager client do <c>site.js</c> dựng
/// cho các bảng <c>[data-paginate]</c> vì cùng dùng chung các class <c>.pager*</c>.
///
/// URL được truyền vào dưới dạng hàm dựng để giữ nguyên mọi bộ lọc hiện có của từng màn hình.
/// </summary>
public sealed class PagerModel
{
    /// <summary>Trang hiện tại (bắt đầu từ 1).</summary>
    public int Page { get; init; }

    /// <summary>Tổng số trang.</summary>
    public int TotalPages { get; init; }

    /// <summary>Số phần tử mỗi trang đang chọn.</summary>
    public int PageSize { get; init; }

    /// <summary>Các lựa chọn "Elements per page".</summary>
    public IReadOnlyList<int> PageSizes { get; init; } = new[] { 10, 50, 100 };

    /// <summary>Dựng URL cho một trang cụ thể (giữ nguyên bộ lọc + pageSize).</summary>
    public required Func<int, string> PageUrl { get; init; }

    /// <summary>Dựng URL khi đổi số phần tử mỗi trang (thường quay về trang 1, giữ nguyên bộ lọc).</summary>
    public required Func<int, string> PageSizeUrl { get; init; }

    /// <summary>
    /// Chuỗi số trang cần hiển thị, chèn <c>-1</c> làm dấu "…". Luôn có trang đầu/cuối,
    /// một cửa sổ quanh trang hiện tại, và mở rộng ở hai đầu để khớp mẫu Bosch (1 2 3 4 5 … N).
    /// </summary>
    public IReadOnlyList<int> PageItems()
    {
        var total = TotalPages;
        var current = Page;
        var items = new List<int>();
        if (total <= 7)
        {
            for (var i = 1; i <= total; i++) items.Add(i);
            return items;
        }

        var start = Math.Max(2, current - 1);
        var end = Math.Min(total - 1, current + 1);
        if (current <= 4) { start = 2; end = 5; }
        if (current >= total - 3) { start = total - 4; end = total - 1; }

        items.Add(1);
        if (start > 2) items.Add(-1);
        for (var i = start; i <= end; i++) items.Add(i);
        if (end < total - 1) items.Add(-1);
        items.Add(total);
        return items;
    }
}
