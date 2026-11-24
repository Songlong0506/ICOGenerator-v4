using System.Globalization;
using System.Resources;

namespace ICOGenerator.Localization;

/// <summary>
/// Nơi DUY NHẤT backend lấy câu chữ viết cho NGƯỜI — lượt BA dựng sẵn, thông báo lỗi của controller,
/// tiêu đề thông báo, chữ trong file xuất. Câu chữ nằm ở <c>Localization/Text.resx</c> (tiếng Việt, bản
/// gốc) và <c>Text.&lt;mã&gt;.resx</c> cho từng ngôn ngữ thêm sau (<c>Text.en.resx</c>); code chỉ giữ KHOÁ.
///
/// <para>
/// <b>Ngôn ngữ giao diện là tiếng Anh, trừ màn Requirements.</b> <see cref="DefaultLanguage"/> là <c>en</c>:
/// mọi request, worker nền và thông báo nói tiếng Anh nếu không ai gọi <see cref="Use"/>. Màn Requirements
/// (chat BA với người dùng nghiệp vụ) gắn <see cref="UiLanguageAttribute"/> để nói tiếng Việt. Khoá chỉ
/// thuộc phía yêu cầu không có bản ở <c>Text.en.resx</c> nên luôn rơi về tiếng Việt của <c>Text.resx</c> —
/// danh sách tiền tố đó chốt ở <c>UiLanguageTests</c>.
/// </para>
///
/// <para>
/// <b>Vì sao static.</b> Phần lớn chốt chặn phía yêu cầu là hàm thuần static (cổng, builder, parser) và
/// được test như hàm thuần. Bắt chúng nhận <c>IStringLocalizer</c> qua DI là đổi chữ ký của cả tầng chỉ để
/// đọc một chuỗi. Ngôn ngữ đang dùng đi theo <see cref="AsyncLocal{T}"/> — cùng cách
/// <see cref="CultureInfo.CurrentUICulture"/> đi theo luồng async — nên hàm static vẫn đọc đúng ngôn ngữ
/// của request / lượt chat / task nền đang chạy nó.
/// </para>
///
/// <para>
/// <b>Vì sao KHÔNG đọc thẳng <see cref="CultureInfo.CurrentUICulture"/>.</b> Culture của tiến trình là của
/// máy chủ (thường <c>en-US</c> hoặc invariant trong container). Đọc nó thì tiếng của app đi theo máy chủ
/// đang chạy nó, không theo màn hình đang phục vụ. Ngôn ngữ ở đây chỉ đổi khi có người gọi
/// <see cref="Use"/>; không ai gọi ⇒ <see cref="DefaultLanguage"/>.
/// </para>
///
/// <para>
/// <b>Không dùng cho chữ gửi MODEL.</b> Prompt và các khối ngữ cảnh ghép cho LLM giữ nguyên trong
/// <c>Prompts/</c> và trong code: model đọc được mọi ngôn ngữ, còn dịch prompt là đổi hành vi đã đo.
/// </para>
/// </summary>
public static class Loc
{
    /// <summary>Ngôn ngữ của <c>Text.resx</c> (bản gốc) — chỗ rơi về khi một khoá không có bản dịch.</summary>
    public const string SourceLanguage = "vi";

    /// <summary>
    /// Ngôn ngữ giao diện khi không ai gọi <see cref="Use"/>: tiếng Anh. Chỉ màn Requirements đổi sang
    /// <see cref="SourceLanguage"/> (xem <see cref="UiLanguageAttribute"/>).
    /// </summary>
    public const string DefaultLanguage = "en";

    /// <summary>
    /// Các ngôn ngữ app nhận. Thêm một ngôn ngữ = thêm <c>Text.&lt;mã&gt;.resx</c> + một phần tử ở đây.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedLanguages = new[] { DefaultLanguage, SourceLanguage };

    private static readonly ResourceManager Resources = new("ICOGenerator.Localization.Text", typeof(Loc).Assembly);

    private static readonly AsyncLocal<CultureInfo?> Current = new();

    /// <summary>Mã ngôn ngữ đang dùng trong luồng async hiện tại.</summary>
    public static string Language => (Current.Value ?? DefaultCulture).Name;

    private static readonly CultureInfo DefaultCulture = CultureInfo.GetCultureInfo(DefaultLanguage);

    /// <summary>Chuỗi của <paramref name="key"/> theo ngôn ngữ đang dùng. Thiếu khoá ⇒ trả chính khoá.</summary>
    public static string T(string key) => Lookup(key, Current.Value ?? DefaultCulture);

    /// <summary>
    /// Như <see cref="T(string)"/> rồi điền tham số theo cú pháp <see cref="string.Format(string, object?[])"/>.
    /// Số và ngày định dạng theo culture của TIẾN TRÌNH, đúng như một chuỗi nội suy <c>$"…"</c> viết tại
    /// chỗ — chuyển một chuỗi sang resource không được làm đổi dấu phân cách hàng nghìn.
    /// </summary>
    public static string T(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>Chuỗi của <paramref name="key"/> ở MỘT ngôn ngữ cụ thể, bất kể ngôn ngữ đang dùng.</summary>
    public static string In(string language, string key) => Lookup(key, Culture(language));

    /// <summary>
    /// Chuỗi của <paramref name="key"/> ở MỌI ngôn ngữ được hỗ trợ — cho những chỗ phải NHẬN RA một nhãn
    /// do hệ thống tự viết (nhãn nhóm bao phủ model chép lại) mà không biết nó được viết bằng ngôn ngữ nào.
    /// </summary>
    public static IEnumerable<string> InAllLanguages(string key)
        => SupportedLanguages.Select(language => In(language, key)).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Đổi ngôn ngữ cho phần còn lại của luồng async hiện tại; <c>Dispose</c> trả lại ngôn ngữ trước đó.
    /// Mã lạ hoặc rỗng ⇒ <see cref="DefaultLanguage"/>.
    /// </summary>
    public static IDisposable Use(string? language)
    {
        var previous = Current.Value;
        Current.Value = Culture(language);
        return new Scope(previous);
    }

    /// <summary>Kéo một mã ngôn ngữ bất kỳ về một phần tử của <see cref="SupportedLanguages"/>.</summary>
    public static string Normalize(string? language)
    {
        var value = (language ?? string.Empty).Trim().ToLowerInvariant();
        return SupportedLanguages.FirstOrDefault(x => value == x || value.StartsWith(x + "-", StringComparison.Ordinal))
               ?? DefaultLanguage;
    }

    private static CultureInfo Culture(string? language) => CultureInfo.GetCultureInfo(Normalize(language));

    private static string Lookup(string key, CultureInfo culture) => Resources.GetString(key, culture) ?? key;

    private sealed class Scope : IDisposable
    {
        private readonly CultureInfo? _previous;

        public Scope(CultureInfo? previous) => _previous = previous;

        public void Dispose() => Current.Value = _previous;
    }
}
