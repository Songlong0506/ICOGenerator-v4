using Microsoft.AspNetCore.Html;

namespace ICOGenerator.Application.Shared;

/// <summary>
/// View model cho command bar dùng chung (mẫu Bosch), render qua partial <c>Shared/_CommandBar</c>.
/// <b>Mọi</b> thanh lệnh cấp trang đều đi qua đây — không màn hình nào tự dựng markup thanh lệnh nữa,
/// vì đó chính là lý do trước kia mỗi màn một kiểu (nhãn khác nhau, nút chính khi trái khi phải,
/// chiều cao control lệch nhau).
///
/// <para><b>Bố cục chuẩn — hai vùng, không có ngoại lệ:</b></para>
/// <code>
/// [ bộ lọc · tìm kiếm ] ···········khoảng trống co giãn··········· [ nút phụ ] [ NÚT CHÍNH ]
///         vùng trái (.cbar-left)                                    vùng phải (.cbar-right)
/// [ chip: Org Unit: PS-EC ✕ ] [ Xóa tất cả ]                        hàng chip (.cbar-chips)
/// </code>
/// <list type="bullet">
///   <item>Vùng trái chỉ chứa thứ <i>thu hẹp dữ liệu đang xem</i>: dropdown lọc, ô tìm kiếm,
///         nút bật panel lọc.</item>
///   <item>Vùng phải chỉ chứa <i>hành động</i>: các nút phụ rồi tới đúng <b>một</b> nút chính sát mép phải.</item>
///   <item>Nút hành động cấp trang không được nằm trong <c>.page-head</c> nữa — chỗ của chúng là vùng phải.</item>
///   <item>Hàng chip nằm dưới cùng, chỉ hiện khi đang có bộ lọc áp dụng.</item>
/// </list>
///
/// <para><b>Không có nút "Lọc" trên thanh.</b> Bộ lọc áp dụng ngay khi người dùng chốt lựa chọn:
/// dropdown đơn tự submit form GET (<c>onchange="this.form.submit()"</c>), combo chọn nhiều tự submit
/// khi đóng panel (nút "Áp dụng" trong chính panel — xem <c>wwwroot/js/projects.js</c>). Lý do: một
/// lựa chọn đã là một ý định trọn vẹn, bắt bấm thêm một nút chỉ tạo ra trạng thái "đã chọn nhưng chưa
/// áp dụng" — lúc đó dropdown và bảng dữ liệu nói hai điều khác nhau. Nút commit, khi cần, phải nằm
/// cạnh nơi phát sinh lựa chọn chứ không nằm rời trên thanh.</para>
///
/// <para>Tìm kiếm chạy phía client trên <see cref="TargetSelector"/> (một bảng/danh sách) do
/// <c>command-bar.js</c> điều khiển — không đụng tới bộ lọc phía server. Bộ lọc server hoặc đặt thẳng
/// lên thanh qua <see cref="Filters"/> + <see cref="FormUrl"/> (form GET), hoặc giấu trong panel và bật
/// bằng nút "Lọc" qua <see cref="FilterPanelId"/>.</para>
/// </summary>
public sealed class CommandBarModel
{
    // ---------------------------------------------------------------- vùng trái: bộ lọc

    /// <summary>
    /// Các trường lọc nội tuyến (select/combo) đặt đầu vùng trái. Truyền bằng templated delegate
    /// trong view — mỗi trường bọc trong <c>&lt;div class="cbar-field"&gt;</c>:
    /// <code>Filters = @@&lt;div class="cbar-field"&gt;…&lt;/div&gt;</code>
    /// </summary>
    public Func<object, IHtmlContent>? Filters { get; init; }

    /// <summary>
    /// URL đích của form GET khi thanh có bộ lọc server-side (vd <c>Url.Action("Index")</c>).
    /// Khác null thì thanh render bằng thẻ <c>&lt;form method="get"&gt;</c> để mọi trường lọc được gửi lên.
    /// </summary>
    public string? FormUrl { get; init; }

    /// <summary>
    /// Chip mô tả bộ lọc <b>đang</b> áp dụng, mỗi chip gỡ được riêng một giá trị. Rỗng thì không
    /// render hàng chip. Chip trả lời câu hỏi <i>tôi đang xem tập dữ liệu nào</i> — thứ mà một nút
    /// "xóa hết" không nói được — và cho gỡ từng tiêu chí thay vì xóa sạch cả cụm.
    /// </summary>
    public IReadOnlyList<CommandBarChip> FilterChips { get; init; } = Array.Empty<CommandBarChip>();

    /// <summary>
    /// URL xóa sạch bộ lọc (thường là chính trang không kèm query). Chỉ render khi có <b>từ 2 chip
    /// trở lên</b> — một chip thì dấu ✕ của chính nó đã đủ, thêm "Xóa tất cả" chỉ là hai nút cùng việc.
    /// </summary>
    public string? ClearFilterUrl { get; init; }

    /// <summary>Placeholder ô tìm kiếm.</summary>
    public string SearchPlaceholder { get; init; } = "Tìm kiếm…";

    /// <summary>
    /// CSS selector của bảng/danh sách để tìm kiếm client-side (vd "#modelsTable").
    /// Null thì ẩn ô tìm kiếm.
    /// </summary>
    public string? TargetSelector { get; init; }

    /// <summary>Id panel lọc (server-side) mà nút "Lọc" bật/tắt. Null thì ẩn nút.</summary>
    public string? FilterPanelId { get; init; }

    /// <summary>Panel lọc mở sẵn khi tải trang (vd đang có bộ lọc áp dụng).</summary>
    public bool FilterPanelOpen { get; init; }

    /// <summary>Nút "Lọc" hiện chấm nhấn mạnh đang có bộ lọc áp dụng.</summary>
    public bool FilterActive { get; init; }

    // ---------------------------------------------------------------- vùng phải: hành động

    /// <summary>
    /// Nút hành động phụ, xếp sát bên trái nút chính (vd "Scenario", "Đánh dấu tất cả đã đọc").
    /// Kiểu viền nhạt để không tranh chấp với nút chính.
    /// </summary>
    public IReadOnlyList<CommandBarButton> Actions { get; init; } = Array.Empty<CommandBarButton>();

    /// <summary>
    /// Hành động chính duy nhất của trang, luôn nằm sát mép phải
    /// (thường là "New …" / "Add …" / "Save" / "Chạy …").
    /// </summary>
    public CommandBarButton? Primary { get; init; }

    // ---------------------------------------------------------------- tính toán cho partial

    public bool HasSearch => TargetSelector != null;

    /// <summary>Thanh render bằng thẻ form (có bộ lọc server-side) hay thẻ div.</summary>
    public bool IsForm => FormUrl != null;

    /// <summary>Vùng trái có gì để render không (không thì vẫn giữ để đẩy vùng phải sang mép phải).</summary>
    public bool HasLeftZone => Filters != null || HasSearch || FilterPanelId != null;

    /// <summary>Có hàng chip bộ lọc để render không.</summary>
    public bool HasFilterChips => FilterChips.Count > 0;

    /// <summary>Hiện link "Xóa tất cả" — chỉ khi nhiều hơn một chip (xem <see cref="ClearFilterUrl"/>).</summary>
    public bool ShowClearAll => ClearFilterUrl != null && FilterChips.Count > 1;

    /// <summary>Vùng phải có gì để render không.</summary>
    public bool HasRightZone => Actions.Count > 0 || Primary != null;
}

/// <summary>
/// Một chip bộ lọc đang áp dụng, render ở hàng dưới cùng của command bar dạng
/// <c>Org Unit: PS-EC ✕</c>. Bấm ✕ đi tới <see cref="RemoveUrl"/> — URL của chính trang với giá trị
/// đó đã bị gỡ khỏi query, các giá trị còn lại giữ nguyên.
/// </summary>
public sealed class CommandBarChip
{
    /// <summary>Tên trường lọc (vd "Org Unit", "Loại"). Hiện mờ phía trước giá trị.</summary>
    public required string Caption { get; init; }

    /// <summary>Giá trị đang lọc (vd tên đơn vị).</summary>
    public required string Value { get; init; }

    /// <summary>URL của trang sau khi gỡ riêng giá trị này khỏi bộ lọc.</summary>
    public required string RemoveUrl { get; init; }
}

/// <summary>
/// Một nút trên command bar. Dùng chung cho nút phụ (vùng phải, kiểu viền nhạt) và nút chính
/// (vùng phải, nền xanh) — kiểu dáng do partial quyết định theo vị trí, view chỉ khai báo nội dung.
/// </summary>
public sealed class CommandBarButton
{
    /// <summary>Nhãn hiển thị.</summary>
    public required string Label { get; init; }

    /// <summary>Class Bootstrap Icons (vd "bi-plus-lg"). Null thì không icon.</summary>
    public string? Icon { get; init; }

    /// <summary>Biểu thức onclick (vd "openModal('addModel')"). Bỏ qua khi có <see cref="Href"/>.</summary>
    public string? OnClick { get; init; }

    /// <summary>Điều hướng: khác null thì nút render thành thẻ <c>&lt;a&gt;</c>.</summary>
    public string? Href { get; init; }

    /// <summary>Id phần tử, cho JS của màn hình gắn handler (vd "pocShareOpen").</summary>
    public string? Id { get; init; }

    /// <summary>Nút mở hộp thoại — gắn <c>aria-haspopup="dialog"</c> + <c>aria-expanded="false"</c>.</summary>
    public bool OpensDialog { get; init; }

    /// <summary>Con số/nhãn nhỏ đính kèm (vd số điểm kỹ thuật chưa đạt). Null thì không hiện.</summary>
    public string? Badge { get; init; }

    /// <summary>title của badge.</summary>
    public string? BadgeTitle { get; init; }

    /// <summary>Vô hiệu hoá nút (vd "Chạy eval" khi chưa có scenario nào bật).</summary>
    public bool Disabled { get; init; }

    /// <summary>title/tooltip.</summary>
    public string? Title { get; init; }
}
