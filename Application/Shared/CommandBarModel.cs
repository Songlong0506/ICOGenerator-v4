namespace ICOGenerator.Application.Shared;

/// <summary>
/// View model cho command bar dùng chung (mẫu Bosch). Mỗi màn hình danh sách/bảng
/// dựng MỘT thanh command bar ở đầu trang qua partial <c>Shared/_CommandBar</c>:
/// đếm số mục (trái) · ô tìm kiếm · nút mở bảng lọc · sắp xếp · các nút pill tuỳ biến ·
/// menu tràn (⋮) · nút hành động chính "+" (phải).
///
/// Tìm kiếm và sắp xếp chạy phía client trên phần tử <see cref="TargetSelector"/>
/// (một bảng hoặc danh sách) do <c>command-bar.js</c> điều khiển — không đụng tới
/// bộ lọc phía server sẵn có của màn hình. Bộ lọc server (nếu có) được đặt trong một
/// panel ẩn và nút "Filter" bật/tắt panel đó qua <see cref="FilterPanelId"/>.
/// </summary>
public sealed class CommandBarModel
{
    /// <summary>Số mục hiển thị bên trái (vd 11 → "11 items"). Null thì ẩn.</summary>
    public int? Count { get; init; }

    /// <summary>Danh từ đi kèm số đếm ("items", "projects", …).</summary>
    public string ItemNoun { get; init; } = "items";

    /// <summary>Ghi đè toàn bộ text đếm khi cần (vd "Showing 1–20 of 120").</summary>
    public string? CountText { get; init; }

    /// <summary>Bật ô tìm kiếm (lọc client-side trên <see cref="TargetSelector"/>).</summary>
    public bool ShowSearch { get; init; } = true;

    /// <summary>Placeholder ô tìm kiếm.</summary>
    public string SearchPlaceholder { get; init; } = "Search";

    /// <summary>
    /// CSS selector của bảng/danh sách để tìm kiếm, sắp xếp và đếm (vd "#projectsTable").
    /// Bắt buộc khi bật tìm kiếm hoặc sắp xếp client-side.
    /// </summary>
    public string? TargetSelector { get; init; }

    /// <summary>Các tuỳ chọn sắp xếp client-side. Rỗng thì ẩn nút Sort.</summary>
    public IReadOnlyList<CommandBarSort> SortOptions { get; init; } = Array.Empty<CommandBarSort>();

    /// <summary>
    /// Id của phần tử panel lọc (server-side) mà nút "Filter" sẽ bật/tắt. Null thì ẩn nút Filter.
    /// </summary>
    public string? FilterPanelId { get; init; }

    /// <summary>Panel lọc mở sẵn khi tải trang (vd đang có bộ lọc áp dụng).</summary>
    public bool FilterPanelOpen { get; init; }

    /// <summary>Nút "Filter" hiện chấm nhấn mạnh đang có bộ lọc áp dụng.</summary>
    public bool FilterActive { get; init; }

    /// <summary>Các nút pill phụ (toggle / link / hành động tuỳ biến) nằm giữa thanh.</summary>
    public IReadOnlyList<CommandBarPill> Pills { get; init; } = Array.Empty<CommandBarPill>();

    /// <summary>Các mục trong menu tràn (⋮). Rỗng thì ẩn menu.</summary>
    public IReadOnlyList<CommandBarPill> OverflowItems { get; init; } = Array.Empty<CommandBarPill>();

    /// <summary>Nút hành động chính "+" bên phải (thường là "New …" / "Add …").</summary>
    public CommandBarPill? Primary { get; init; }

    public bool HasSort => SortOptions.Count > 0;
}

/// <summary>Một tuỳ chọn sắp xếp client-side cho bảng/danh sách.</summary>
public sealed class CommandBarSort
{
    /// <summary>Nhãn hiển thị trong menu Sort (vd "Name A→Z").</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Với bảng: chỉ số cột (0-based) để so sánh. Với danh sách: bỏ qua và dùng <see cref="Key"/>.
    /// </summary>
    public int Column { get; init; }

    /// <summary>data-key trên mỗi item (danh sách không phải bảng) để đọc giá trị sắp xếp.</summary>
    public string? Key { get; init; }

    /// <summary>Sắp xếp giảm dần.</summary>
    public bool Descending { get; init; }

    /// <summary>So sánh dạng số thay vì chuỗi.</summary>
    public bool Numeric { get; init; }
}

/// <summary>Kiểu hiển thị nút pill trên command bar.</summary>
public enum CommandBarPillKind
{
    /// <summary>Nút thường (mặc định) — thường mở modal qua OnClick.</summary>
    Button,

    /// <summary>Liên kết điều hướng (Href).</summary>
    Link,

    /// <summary>Nút toggle có trạng thái bật/tắt (dùng <see cref="CommandBarPill.Active"/>).</summary>
    Toggle
}

/// <summary>Một nút/mục trên command bar (pill giữa thanh, mục menu tràn, hoặc nút chính).</summary>
public sealed class CommandBarPill
{
    /// <summary>Nhãn hiển thị.</summary>
    public required string Label { get; init; }

    /// <summary>Class Bootstrap Icons (vd "bi-funnel"). Null thì không icon.</summary>
    public string? Icon { get; init; }

    public CommandBarPillKind Kind { get; init; } = CommandBarPillKind.Button;

    /// <summary>Đích điều hướng khi <see cref="Kind"/> = Link.</summary>
    public string? Href { get; init; }

    /// <summary>Biểu thức onclick khi <see cref="Kind"/> = Button (vd "openModal('addModel')").</summary>
    public string? OnClick { get; init; }

    /// <summary>Trạng thái bật của nút Toggle.</summary>
    public bool Active { get; init; }

    /// <summary>Nhấn mạnh nút (dùng cho hành động chính hoặc pill nổi bật).</summary>
    public bool Emphasize { get; init; }

    /// <summary>Class variant thêm cho hành động nguy hiểm/thành công ("danger", "success").</summary>
    public string? Variant { get; init; }

    /// <summary>title/tooltip.</summary>
    public string? Title { get; init; }
}
