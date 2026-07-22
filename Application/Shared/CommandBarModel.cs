namespace ICOGenerator.Application.Shared;

/// <summary>
/// View model cho command bar dùng chung (mẫu Bosch), render qua partial
/// <c>Shared/_CommandBar</c>. Chỉ những màn hình danh sách/bảng cần tìm kiếm/sắp xếp
/// mới dùng partial này (Models, Audit, Roles, Notifications, Evals); các màn có bộ lọc
/// riêng biệt (Projects, Feedback, User Roles) tự dựng thanh bằng class <c>.command-bar</c>.
///
/// Tìm kiếm và sắp xếp chạy phía client trên <see cref="TargetSelector"/> (một bảng/danh
/// sách) do <c>command-bar.js</c> điều khiển — không đụng tới bộ lọc phía server. Bộ lọc
/// server (nếu có) đặt trong panel ẩn và nút "Filter" bật/tắt panel đó qua <see cref="FilterPanelId"/>.
/// </summary>
public sealed class CommandBarModel
{
    /// <summary>Placeholder ô tìm kiếm.</summary>
    public string SearchPlaceholder { get; init; } = "Search";

    /// <summary>
    /// CSS selector của bảng/danh sách để tìm kiếm và sắp xếp (vd "#modelsTable").
    /// Null thì ẩn ô tìm kiếm và nút Sort.
    /// </summary>
    public string? TargetSelector { get; init; }

    /// <summary>Id panel lọc (server-side) mà nút "Filter" bật/tắt. Null thì ẩn nút Filter.</summary>
    public string? FilterPanelId { get; init; }

    /// <summary>Panel lọc mở sẵn khi tải trang (vd đang có bộ lọc áp dụng).</summary>
    public bool FilterPanelOpen { get; init; }

    /// <summary>Nút "Filter" hiện chấm nhấn mạnh đang có bộ lọc áp dụng.</summary>
    public bool FilterActive { get; init; }

    /// <summary>Các nút phụ giữa thanh (vd "Scenario").</summary>
    public IReadOnlyList<CommandBarButton> Pills { get; init; } = Array.Empty<CommandBarButton>();

    /// <summary>Nút hành động chính bên phải (thường là "New …" / "Add …" / "Chạy …").</summary>
    public CommandBarButton? Primary { get; init; }

    public bool HasSearch => TargetSelector != null;
}

/// <summary>Một nút trên command bar (pill giữa thanh hoặc nút chính) — luôn mở modal qua OnClick.</summary>
public sealed class CommandBarButton
{
    /// <summary>Nhãn hiển thị.</summary>
    public required string Label { get; init; }

    /// <summary>Class Bootstrap Icons (vd "bi-plus-lg"). Null thì không icon.</summary>
    public string? Icon { get; init; }

    /// <summary>Biểu thức onclick (vd "openModal('addModel')").</summary>
    public string? OnClick { get; init; }

    /// <summary>Vô hiệu hoá nút (vd "Chạy eval" khi chưa có scenario nào bật).</summary>
    public bool Disabled { get; init; }

    /// <summary>title/tooltip.</summary>
    public string? Title { get; init; }
}
