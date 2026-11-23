namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// BẢN THIẾT KẾ UX của ứng dụng (<c>Project.UxBlueprint</c>) — nguồn DUY NHẤT của phạm vi màn hình.
///
/// <para>
/// <b>Tách "phải làm được gì" khỏi "tổ chức thế nào".</b> Bản thiết kế có ba tầng, mỗi tầng một câu hỏi:
/// </para>
/// <list type="number">
///   <item><see cref="Objects"/> — ứng dụng quản lý NHỮNG BẢN GHI nào, mỗi bản ghi đi qua những trạng thái
///   nào. Thiết kế UI tốt cho app nghiệp vụ là thiết kế quanh đối tượng, không quanh câu chữ người dùng kể.</item>
///   <item><see cref="Tasks"/> — danh mục TÁC VỤ (ai làm gì trên bản ghi nào) có ID ỔN ĐỊNH. Đây là chỗ giữ
///   truy vết: mỗi bước luồng người dùng đã chốt phải có tác vụ nhận, và bảng phân quyền khoá theo ID tác vụ
///   chứ không theo tên màn hình.</item>
///   <item><see cref="Screens"/> + <see cref="Navigation"/> — cách tổ chức các tác vụ thành màn hình: dạng
///   màn (danh sách, chi tiết, hàng chờ duyệt…), hành động nào đặt ở đâu, mỗi vai vào ứng dụng từ đâu.</item>
/// </list>
///
/// <para>
/// <b>Vì sao khoá theo tác vụ.</b> Khi tên màn hình còn là khoá nối của bảng phân quyền, gộp hay tách một màn
/// là vỡ phân quyền — nên bảng màn hình cũ chỉ được THÊM, và một khi đã chốt thì không bao giờ tối ưu lại
/// được. Khoá theo tác vụ thì màn hình chỉ là CÁCH TỔ CHỨC: thiết kế lại cấu trúc không đụng tới quyền.
/// </para>
///
/// <para>
/// <see cref="Journeys"/>, <see cref="Metrics"/> và <see cref="Findings"/> là phần TẤT ĐỊNH do code tính từ
/// ba tầng trên (<c>UxJourneyAnalyzer</c>, <c>UxBlueprintLint</c>) — không model nào được ghi vào đó.
/// </para>
/// </summary>
public class UxBlueprint
{
    /// <summary>Số lần đã thiết kế (tăng mỗi lượt <c>UxDesignService</c> ghi bảng). 0 = chưa thiết kế lần nào.</summary>
    public int Version { get; set; }

    /// <summary>
    /// Số lớn nhất đã từng cấp cho ID tác vụ / màn hình. ID đã cấp không bao giờ được dùng lại cho việc khác,
    /// kể cả khi tác vụ cũ đã bị bỏ — một quyền còn sót của ID cũ sẽ dính sang việc mới mà không ai thấy.
    /// </summary>
    public int LastTaskNumber { get; set; }

    public int LastScreenNumber { get; set; }

    public List<UxObject> Objects { get; set; } = new();

    public List<UxTask> Tasks { get; set; } = new();

    public List<UxScreen> Screens { get; set; } = new();

    /// <summary>Điều hướng THEO VAI: mỗi vai một cây menu + một trang đích.</summary>
    public List<UxNavigation> Navigation { get; set; } = new();

    /// <summary>Hành trình của từng luồng nghiệp vụ qua các màn hình — tính tất định, xem <c>UxJourneyAnalyzer</c>.</summary>
    public List<UxJourney> Journeys { get; set; } = new();

    public UxMetrics Metrics { get; set; } = new();

    /// <summary>Lỗi/cảnh báo thiết kế còn lại SAU vòng phản biện và vòng vá tất định — xem <c>UxBlueprintLint</c>.</summary>
    public List<UxFinding> Findings { get; set; } = new();

    /// <summary>
    /// Góp ý THIẾT KẾ người dùng đã gửi (từ POC Review): "gộp hai màn này", "nút này nên ở trang chi tiết".
    /// Sống qua mọi lần thiết kế lại — không có danh sách này thì lần thiết kế sau dựng lại đúng cấu trúc
    /// người dùng vừa chê.
    /// </summary>
    public List<UxDesignNote> DesignNotes { get; set; } = new();
}

/// <summary>Một loại BẢN GHI ứng dụng quản lý (tủ, phiếu, JD…), kèm vòng đời của nó.</summary>
public class UxObject
{
    /// <summary>Tên đối tượng, tiếng Anh Title Case ("Locker", "Repair Ticket") — cùng luật tên với spec/POC.</summary>
    public string Name { get; set; } = "";

    /// <summary>Đối tượng này là gì, một câu theo lời nghiệp vụ.</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Là một DANH MỤC (danh sách để chọn: khu vực, loại phiếu…) chứ không phải bản ghi nghiệp vụ có vòng đời.
    /// Danh mục đơn giản gom chung một màn Master Data có tab — xem pattern library của UiUx.
    /// </summary>
    public bool IsCatalog { get; set; }

    /// <summary>Các trạng thái theo thứ tự vòng đời (tiếng Anh Title Case). Rỗng với danh mục.</summary>
    public List<string> States { get; set; } = new();

    /// <summary>Ước lượng số thông tin nhập tay của form tạo/sửa — quyết định modal hay trang riêng.</summary>
    public int FieldCount { get; set; }

    /// <summary>Có danh sách con trong bản ghi (dòng chi tiết, các lớp của một kế hoạch…) — form lớn, không vừa modal.</summary>
    public bool HasLineItems { get; set; }
}

/// <summary>Các loại tác vụ. Lưu dạng chuỗi trong JSON — KHÔNG đổi giá trị đã có.</summary>
public static class UxTaskKind
{
    public const string Browse = "browse";        // xem danh sách / tra cứu
    public const string View = "view";            // xem chi tiết một bản ghi
    public const string Create = "create";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Transition = "transition"; // đổi trạng thái (gửi duyệt, duyệt, thu hồi…)
    public const string Bulk = "bulk";            // thao tác hàng loạt
    public const string Import = "import";
    public const string Export = "export";
    public const string Report = "report";        // một báo cáo / thống kê
    public const string Configure = "configure";  // cấu hình / quản lý danh mục

    public static readonly string[] All =
        { Browse, View, Create, Edit, Delete, Transition, Bulk, Import, Export, Report, Configure };
}

/// <summary>Nguồn của một tác vụ — tác vụ nào cũng phải truy được về một điều người dùng đã nói.</summary>
public static class UxTaskSource
{
    public const string Flow = "flow";                 // một bước của bảng luồng người dùng đã chốt
    public const string Report = "report";             // một báo cáo của bảng báo cáo
    public const string Catalog = "catalog";           // quản lý một danh mục ứng dụng tự quản lý
    public const string Conversation = "conversation"; // một thao tác người dùng kể trong hội thoại
    public const string Feedback = "feedback";         // góp ý ở POC Review

    public static readonly string[] All = { Flow, Report, Catalog, Conversation, Feedback };
}

/// <summary>
/// MỘT tác vụ: MỘT việc một vai làm trên một loại bản ghi. Đơn vị mà bảng phân quyền cấp quyền và mà màn hình
/// đặt thành một hành động.
/// </summary>
public class UxTask
{
    /// <summary>ID ổn định ("T-07") — code cấp, không model nào đặt; giữ nguyên qua mọi lần thiết kế lại.</summary>
    public string Id { get; set; } = "";

    /// <summary>Tên tác vụ theo lời nghiệp vụ (tiếng Việt), MỘT việc: "Thu hồi tủ khi nhân viên trả".</summary>
    public string Name { get; set; } = "";

    /// <summary>Tên đối tượng (<see cref="UxObject.Name"/>) tác vụ làm trên đó; rỗng với tác vụ không gắn bản ghi.</summary>
    public string Object { get; set; } = "";

    /// <summary>Một giá trị của <see cref="UxTaskKind"/>.</summary>
    public string Kind { get; set; } = UxTaskKind.Browse;

    /// <summary>Các vai làm tác vụ này.</summary>
    public List<string> Actors { get; set; } = new();

    /// <summary>Các bước của bảng luồng đã chốt tác vụ này phụ trách — chép phần <c>action</c> của bước.</summary>
    public List<string> FlowSteps { get; set; } = new();

    /// <summary>Trạng thái trước / sau (tác vụ chuyển trạng thái). Rỗng khi không đổi trạng thái.</summary>
    public string FromState { get; set; } = "";

    public string ToState { get; set; } = "";

    /// <summary>Một giá trị của <see cref="UxTaskSource"/>.</summary>
    public string Source { get; set; } = UxTaskSource.Conversation;

    /// <summary>
    /// Đã được một lượt thiết kế xếp chỗ chưa. <c>false</c> = tác vụ vừa lộ ra từ hội thoại (lượt chắt lọc phạm
    /// vi) mà chưa có màn hình nào nhận — là điều kiện mở của <c>UxDesignGate</c>.
    /// </summary>
    public bool Placed { get; set; }
}

/// <summary>Các dạng màn hình của pattern library. Lưu dạng chuỗi — KHÔNG đổi giá trị đã có.</summary>
public static class UxPattern
{
    public const string Dashboard = "dashboard";     // trang tổng quan: thẻ số, biểu đồ, gom báo cáo
    public const string List = "list";               // danh sách bản ghi + lọc + hành động trên dòng
    public const string ListDetail = "list-detail";  // master-detail: danh sách + khung chi tiết cạnh bên
    public const string Detail = "detail";           // trang chi tiết một bản ghi (mở từ danh sách)
    public const string Form = "form";               // trang nhập liệu riêng (form lớn / có dòng con)
    public const string Wizard = "wizard";           // nhập liệu nhiều bước
    public const string Queue = "queue";             // hàng chờ xử lý (duyệt) của một vai
    public const string Board = "board";             // bảng kanban theo trạng thái
    public const string Calendar = "calendar";       // lịch
    public const string MasterData = "master-data";  // nhiều danh mục đơn giản gom thành tab
    public const string Report = "report";           // báo cáo riêng có bộ lọc + xuất

    public static readonly string[] All =
        { Dashboard, List, ListDetail, Detail, Form, Wizard, Queue, Board, Calendar, MasterData, Report };

    /// <summary>Dạng màn đóng vai "NHÀ" của một đối tượng — nơi người dùng tìm thấy bản ghi đó.</summary>
    public static readonly string[] Home = { List, ListDetail, Board, Calendar, MasterData };
}

/// <summary>Vị trí đặt một hành động trên màn hình. Lưu dạng chuỗi — KHÔNG đổi giá trị đã có.</summary>
public static class UxPlacement
{
    public const string Toolbar = "toolbar";            // thanh công cụ đầu màn (Thêm, Xuất, Nhập)
    public const string Row = "row";                    // nút trên từng dòng của danh sách
    public const string Bulk = "bulk";                  // thao tác trên các dòng đã chọn
    public const string DetailHeader = "detail-header"; // nút ở đầu khung/trang chi tiết bản ghi
    public const string Inline = "inline";              // sửa ngay tại chỗ
    public const string Filter = "filter";              // bộ lọc / tab lọc
    public const string Section = "section";            // một khối nội dung (thẻ số, bảng tổng hợp, biểu đồ)

    public static readonly string[] All = { Toolbar, Row, Bulk, DetailHeader, Inline, Filter, Section };
}

/// <summary>Một HÀNH ĐỘNG trên màn hình: tác vụ nào, nhãn gì, đặt ở đâu, nổi bật tới đâu.</summary>
public class UxAction
{
    /// <summary>ID tác vụ (<see cref="UxTask.Id"/>).</summary>
    public string Task { get; set; } = "";

    /// <summary>Nhãn nút/khối đúng như trên giao diện (theo ngôn ngữ UI của dự án).</summary>
    public string Label { get; set; } = "";

    /// <summary>Một giá trị của <see cref="UxPlacement"/>.</summary>
    public string Placement { get; set; } = UxPlacement.Toolbar;

    /// <summary>"primary" | "secondary" | "overflow" (menu phụ "…").</summary>
    public string Prominence { get; set; } = UxProminence.Secondary;

    /// <summary>Chỉ hiện khi bản ghi ở trạng thái này (nút chuyển trạng thái). Rỗng = luôn hiện.</summary>
    public string WhenState { get; set; } = "";

    /// <summary>Cách mở form nhập liệu của hành động: "modal" | "page" | "none".</summary>
    public string Form { get; set; } = UxFormMode.None;
}

public static class UxProminence
{
    public const string Primary = "primary";
    public const string Secondary = "secondary";
    public const string Overflow = "overflow";

    public static readonly string[] All = { Primary, Secondary, Overflow };
}

public static class UxFormMode
{
    public const string None = "none";
    public const string Modal = "modal";
    public const string Page = "page";

    public static readonly string[] All = { None, Modal, Page };
}

/// <summary>MỘT màn hình của ứng dụng.</summary>
public class UxScreen
{
    /// <summary>ID ổn định ("S-03") — code cấp.</summary>
    public string Id { get; set; } = "";

    /// <summary>Tên màn hình: tiếng Anh, 2–4 từ, danh từ chỉ nơi chốn — nhãn menu của bản demo.</summary>
    public string Name { get; set; } = "";

    /// <summary>Một giá trị của <see cref="UxPattern"/>.</summary>
    public string Pattern { get; set; } = UxPattern.List;

    /// <summary>Đối tượng chính của màn (rỗng với dashboard gom nhiều thứ).</summary>
    public string Object { get; set; } = "";

    /// <summary>Màn hình LÀ GÌ, một câu theo góc nhìn nghiệp vụ.</summary>
    public string Purpose { get; set; } = "";

    /// <summary>Các vai vào được màn này.</summary>
    public List<string> Audience { get; set; } = new();

    /// <summary>"nav" = có mục menu; "drilldown" = chỉ mở từ màn khác (trang chi tiết).</summary>
    public string Entry { get; set; } = UxEntry.Nav;

    /// <summary>Các tab của màn (master-data: mỗi danh mục một tab; danh sách: tab lọc theo trạng thái).</summary>
    public List<string> Tabs { get; set; } = new();

    public List<UxAction> Actions { get; set; } = new();

    /// <summary>Vì sao màn hình được tổ chức như vậy — một câu, để người đọc spec hiểu quyết định thiết kế.</summary>
    public string Rationale { get; set; } = "";
}

public static class UxEntry
{
    public const string Nav = "nav";
    public const string Drilldown = "drilldown";

    public static readonly string[] All = { Nav, Drilldown };
}

/// <summary>Điều hướng của MỘT vai.</summary>
public class UxNavigation
{
    public string Role { get; set; } = "";

    /// <summary>Tên màn hình mở ra đầu tiên khi vai này vào ứng dụng.</summary>
    public string Landing { get; set; } = "";

    public List<UxNavItem> Items { get; set; } = new();
}

/// <summary>Một mục menu: hoặc trỏ vào một màn hình (<see cref="Screen"/>), hoặc là một NHÓM có <see cref="Children"/>.</summary>
public class UxNavItem
{
    public string Label { get; set; } = "";

    /// <summary>Tên màn hình; rỗng khi là tiêu đề nhóm.</summary>
    public string Screen { get; set; } = "";

    public List<UxNavItem> Children { get; set; } = new();
}

/// <summary>Hành trình của MỘT luồng nghiệp vụ qua các màn hình — tính tất định từ bảng luồng + bản thiết kế.</summary>
public class UxJourney
{
    public string Flow { get; set; } = "";

    /// <summary><c>FlowKind</c> của luồng.</summary>
    public string Kind { get; set; } = "";

    public List<UxJourneyStep> Steps { get; set; } = new();

    /// <summary>Số lần người làm phải ĐỔI MÀN giữa hai bước liên tiếp CỦA CHÍNH HỌ.</summary>
    public int ScreenSwitches { get; set; }

    /// <summary>Số lần luồng đổi người làm (bàn giao giữa các vai).</summary>
    public int Handoffs { get; set; }
}

public class UxJourneyStep
{
    public string Step { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Task { get; set; } = "";
    public string Screen { get; set; } = "";
    public string Action { get; set; } = "";

    /// <summary>Bước làm ngoài phần mềm — không có màn hình.</summary>
    public bool Outside { get; set; }
}

/// <summary>Chỉ số TẤT ĐỊNH của một bản thiết kế — để so trước/sau mỗi thay đổi prompt/model (golden set, eval).</summary>
public class UxMetrics
{
    public int Objects { get; set; }
    public int Tasks { get; set; }
    public int Screens { get; set; }
    public int NavScreens { get; set; }

    /// <summary>Số màn hình nghiệp vụ trên mỗi đối tượng KHÔNG phải danh mục (lý tưởng 1–2).</summary>
    public double ScreensPerObject { get; set; }

    public int MaxTopLevelNavItems { get; set; }

    /// <summary>Số lần đổi màn trung bình trên mỗi luồng (chỉ tính bước của cùng một người làm).</summary>
    public double AvgScreenSwitchesPerFlow { get; set; }

    public int ThinScreens { get; set; }
    public int DuplicateHomes { get; set; }
    public int UnplacedTasks { get; set; }
    public int UncoveredFlowSteps { get; set; }
    public int Errors { get; set; }
    public int Warnings { get; set; }
}

/// <summary>Một phát hiện của <c>UxBlueprintLint</c>.</summary>
public class UxFinding
{
    /// <summary>Mã luật (<c>UxLintCode</c>) — ổn định để test và eval bám vào.</summary>
    public string Code { get; set; } = "";

    /// <summary>"error" (vòng phản biện phải sửa) | "warning" (ghi lại, không chặn).</summary>
    public string Severity { get; set; } = UxSeverity.Warning;

    /// <summary>Thứ bị nêu (tên màn, ID tác vụ, tên luồng…).</summary>
    public string Subject { get; set; } = "";

    public string Message { get; set; } = "";
}

public static class UxSeverity
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>Một góp ý THIẾT KẾ người dùng gửi — xem <see cref="UxBlueprint.DesignNotes"/>.</summary>
public class UxDesignNote
{
    public string Text { get; set; } = "";

    /// <summary>Màn hình góp ý nhắm tới (nếu có).</summary>
    public string Screen { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
