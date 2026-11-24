using System.Text.Json.Serialization;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Bộ lái kịch bản nghiệm thu chạy TRONG trang POC (<c>PocUatDriver.js</c>, nhúng vào assembly). Nằm ở file JS
/// riêng thay vì chuỗi trong C# vì nó là một chương trình thật — tìm vai/màn/nút/ô theo nhãn, gõ, chọn, chờ
/// trang phản ứng — và phải đọc/sửa được như code.
/// </summary>
internal static class PocUatDriver
{
    private const string ResourceName = "ICOGenerator.Services.Artifacts.PocUatDriver.js";

    private static readonly Lazy<string> LazyScript = new(() =>
    {
        using var stream = typeof(PocUatDriver).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Thiếu embedded resource {ResourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>Script định nghĩa <c>window.__pocUat</c> — nạp bằng AddInitScript để có ở mọi lần tải trang.</summary>
    public static string Script => LazyScript.Value;

    // Các sự kiện bộ lái trả về. Chỉ là SỰ KIỆN — kết luận do UatScenarioDrive.
    public const string Ok = "ok";                   // làm được, trang phản ứng đúng (hoặc đã ở sẵn trạng thái đó)
    public const string Missing = "missing";         // thứ cần thao tác không có ở đâu trên trang
    public const string Dead = "dead";               // bấm đúng điều khiển mà không có gì đổi
    public const string Teleport = "teleport";       // màn không có trên menu, máy mở thẳng bằng pocNavigate
    public const string Unresolved = "unresolved";   // không làm được (không tìm ra ô, ô không nhận giá trị…)
    public const string NoBootstrap = "nobootstrap"; // modal cần Bootstrap từ CDN mà môi trường offline
    public const string Seen = "seen";               // bước kiểm tra: chữ có trên màn
    public const string NotSeen = "notseen";         // bước kiểm tra: chữ không có trên màn
}

/// <summary>Một sự kiện bộ lái trả về (JSON của <c>window.__pocUat.act/legacy</c>).</summary>
internal sealed class UatDriveOutcome
{
    [JsonPropertyName("status")] public string Status { get; set; } = PocUatDriver.Unresolved;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("note")] public string Note { get; set; } = string.Empty;
    /// <summary>Màn đang mở SAU thao tác (<c>data-view</c> của section active).</summary>
    [JsonPropertyName("view")] public string View { get; set; } = string.Empty;
    /// <summary>Chỉ đường lùi theo câu chữ: số nhãn trong ngoặc của câu bước.</summary>
    [JsonPropertyName("quoted")] public int Quoted { get; set; }

    public static UatDriveOutcome Unknown(string note) => new() { Status = PocUatDriver.Unresolved, Note = note };
}

/// <summary>
/// Chấm MỘT kịch bản từ chuỗi sự kiện của bộ lái. Luật trung tâm — <b>chỉ kết luận khi trạng thái còn tin
/// được</b>: "thiếu điều khiển" và "nút chết" chỉ là khuyết tật của POC khi MỌI thao tác trước đó đều đã làm
/// được. Một khi máy không làm được một thao tác (không tìm ra ô nhập, ô không nhận giá trị, phải mở thẳng
/// một màn không có trên menu, câu bước cũ nêu nhiều nhãn mà máy chỉ bấm được một), trang có thể không ở
/// trạng thái kịch bản giả định — điều khiển của bước sau "không có" có khi chỉ vì máy chưa tới được chỗ của
/// nó. Từ đó trở đi máy vẫn chạy thử nốt nhưng không chấm.
///
/// <para>
/// Lý do tồn tại: trước đây mỗi bước được chấm độc lập. Một câu bước gộp hai thao tác ("Mở 'Danh sách' và bấm
/// dòng X") chỉ được bấm vế đầu, bước sau báo thiếu điều khiển vì nó nằm ở màn máy chưa mở; và một giá trị
/// cần GÕ ("Nhập Tên là 'ABC'") bị tìm như nhãn nút. Cổng UAT mở vòng tự sửa cho những báo động giả đó, và
/// agent sửa bằng cách thêm nút giả mang đúng chữ máy tìm — POC xấu đi mà token vẫn tốn.
/// </para>
/// </summary>
internal sealed class UatScenarioDrive
{
    private const int MaxPathShown = 6;
    private const int MaxNotes = 2;

    private readonly string _title;
    private readonly Func<int> _errorCount;
    private readonly List<string> _done = new();
    private readonly List<string> _notes = new();
    private int _actionsDone;
    private string? _uncertainAt;
    private int? _errorsAtTrustLoss;

    public UatScenarioDrive(string title, Func<int> errorCount)
    {
        _title = title;
        _errorCount = errorCount;
    }

    /// <summary>Trang còn ở đúng trạng thái kịch bản giả định không.</summary>
    public bool Trusted => _uncertainAt == null;

    /// <summary>Khuyết tật đã kết luận (null = chưa có); có rồi thì caller dừng kịch bản.</summary>
    public string? Failure { get; private set; }

    /// <summary>Một thao tác của bước có kế hoạch máy-đọc-được.</summary>
    public void RecordAction(int stepIndex, string step, UatAction action, UatDriveOutcome outcome)
    {
        var what = UatActionKind.Describe(action);
        switch (outcome.Status)
        {
            case PocUatDriver.Ok:
                _actionsDone++;
                _done.Add(what);
                break;
            case PocUatDriver.Seen:
                break;
            case PocUatDriver.NotSeen:
                if (Trusted && _notes.Count < MaxNotes)
                    _notes.Add($"bước {stepIndex + 1} chưa thấy «{action.Target}» trên màn '{outcome.View}'");
                break;
            case PocUatDriver.Teleport:
                _done.Add(what + " (máy mở thẳng)");
                LoseTrust(stepIndex, $"màn «{action.Target}» không có trên menu của vai đang chọn nên máy mở thẳng — ngữ cảnh có thể khác khi người dùng tự đi tới");
                break;
            case PocUatDriver.Missing when Trusted:
                Failure = Located(stepIndex, step, what, MissingReason(action, outcome));
                break;
            case PocUatDriver.Dead when Trusted:
                Failure = Located(stepIndex, step, what, DeadReason(action, outcome));
                break;
            default:
                // Unresolved / NoBootstrap / Missing-Dead khi đã mất tin cậy: không kết luận.
                LoseTrust(stepIndex, string.IsNullOrWhiteSpace(outcome.Note) ? what : $"{what}: {outcome.Note}");
                break;
        }
    }

    /// <summary>
    /// Một bước của bộ kịch bản cũ (chỉ có câu chữ, máy đoán MỘT điều khiển từ câu). Câu nêu từ hai nhãn trong
    /// ngoặc trở lên không đơn nghĩa: máy chỉ bấm được một thứ, nên không kết luận và cũng không còn tin trạng
    /// thái sau bước đó.
    /// </summary>
    public void RecordLegacyStep(int stepIndex, string step, UatDriveOutcome outcome)
    {
        var unambiguous = outcome.Quoted <= 1;
        switch (outcome.Status)
        {
            case PocUatDriver.Ok:
                _actionsDone++;
                _done.Add($"bấm «{outcome.Label}»");
                if (!unambiguous)
                    LoseTrust(stepIndex, "câu bước nêu nhiều nhãn, máy chỉ làm được một thao tác");
                break;
            case PocUatDriver.Missing when Trusted && unambiguous:
                Failure = $"bước {stepIndex + 1} (\"{Shorten(step)}\"): không tìm thấy điều khiển nào mang nhãn '{outcome.Label}' trên POC" + PathSuffix();
                break;
            case PocUatDriver.Dead when Trusted && unambiguous:
                Failure = $"bước {stepIndex + 1} (\"{Shorten(step)}\"): bấm '{outcome.Label}' nhưng màn hình không thay đổi gì (nút chưa nối logic)" + PathSuffix();
                break;
            default:
                LoseTrust(stepIndex, string.IsNullOrWhiteSpace(outcome.Note) ? "máy không đoán được thao tác từ câu bước" : outcome.Note);
                break;
        }
    }

    /// <summary>
    /// Kết quả của kịch bản. Lỗi JS chỉ tính khi phát sinh lúc trạng thái còn tin được — lỗi sau một lần máy
    /// mở thẳng màn hình có thể chỉ vì màn đó thiếu ngữ cảnh mà người dùng thật luôn có.
    /// </summary>
    public PocUatDriveResult Finish(IReadOnlyList<string> errors, int errorsBefore, string? exceptionFailure = null)
    {
        var failure = Failure ?? exceptionFailure;
        if (failure == null)
        {
            var upTo = Math.Min(_errorsAtTrustLoss ?? errors.Count, errors.Count);
            if (upTo > errorsBefore)
                failure = $"lỗi JS khi thao tác: {PlaywrightPocRuntimeChecker.FirstLine(errors[errorsBefore])}" + PathSuffix();
        }

        if (failure != null)
            return new PocUatDriveResult(_title, false, failure);

        var detail = $"{_actionsDone} thao tác chạy được, không lỗi";
        if (_uncertainAt != null)
            detail += $"; không kết luận được từ {_uncertainAt} — phần sau chỉ chạy thử, không chấm";
        if (_notes.Count > 0)
            detail += $"; lưu ý: {string.Join("; ", _notes)}";
        return new PocUatDriveResult(_title, true, detail);
    }

    private void LoseTrust(int stepIndex, string reason)
    {
        if (_uncertainAt != null)
            return;
        _uncertainAt = $"bước {stepIndex + 1} ({reason})";
        _errorsAtTrustLoss = _errorCount();
    }

    private string Located(int stepIndex, string step, string what, string reason) =>
        $"bước {stepIndex + 1} (\"{Shorten(step)}\") — {what}: {reason}" + PathSuffix();

    // Agent sửa không nhớ bản demo: biết máy đã đi đường nào tới chỗ gãy là biết trang đang ở trạng thái nào.
    private string PathSuffix() =>
        _done.Count == 0 ? string.Empty
        : $" (trước đó máy đã: {string.Join(" → ", _done.Skip(Math.Max(0, _done.Count - MaxPathShown)))})";

    private static string MissingReason(UatAction action, UatDriveOutcome outcome) => action.Kind switch
    {
        UatActionKind.Role => $"không có vai «{action.Target}» ở khối VIEW AS",
        UatActionKind.Open => $"POC không có màn hình «{action.Target}»",
        _ => $"không tìm thấy điều khiển nào mang nhãn «{action.Target}» trên POC (màn đang mở: '{outcome.View}', chữ đó cũng không có ở đâu trên trang)"
    };

    private static string DeadReason(UatAction action, UatDriveOutcome outcome) => action.Kind switch
    {
        UatActionKind.Role => $"bấm vai «{action.Target}» ở VIEW AS nhưng vai không đổi",
        UatActionKind.Open => $"bấm mục menu «{action.Target}» nhưng màn đó không mở (vẫn đang ở '{outcome.View}')",
        _ => $"bấm «{outcome.Label}» nhưng màn hình không thay đổi gì (nút chưa nối logic)"
    };

    internal static string Shorten(string text)
    {
        var clean = (text ?? string.Empty).Replace('\n', ' ').Trim();
        return clean.Length <= 80 ? clean : clean[..77] + "…";
    }
}
