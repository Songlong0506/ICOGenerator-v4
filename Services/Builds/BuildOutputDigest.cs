namespace ICOGenerator.Services.Builds;

/// <summary>
/// Rút phần ĐÁNG ĐỌC của một khối output build để nhét vào prompt sửa lỗi.
/// <para>
/// Vì sao không đưa nguyên khối: <c>CommandTools</c> giữ tới 100.000 ký tự mỗi luồng, mà một lần
/// <c>npm run build</c> hay <c>dotnet build</c> hỏng thì 95% trong đó là dòng tiến độ, danh sách
/// package và đường dẫn — nhồi hết vào prompt là đẩy chính mấy dòng lỗi ra khỏi phần model đọc kỹ
/// nhất, và trả tiền token cho phần vô nghĩa.
/// </para>
/// <para>
/// Không nhận ra dòng lỗi nào thì lấy PHẦN ĐUÔI thay vì trả rỗng: công cụ build nào cũng in lý do
/// dừng ở cuối, và một digest rỗng thì task sửa lỗi không còn gì để bám.
/// </para>
/// </summary>
public static class BuildOutputDigest
{
    private const int MaxErrorLines = 60;
    private const int TailLines = 80;
    private const int MaxChars = 8000;

    // Các cụm mà mọi chuỗi công cụ trong whitelist đều dùng để đánh dấu một lỗi thật:
    // Roslyn ("error CS0103:"), TypeScript/Angular ("error TS2307:", "ERROR in"), npm ("npm ERR!").
    private static readonly string[] ErrorMarkers =
    [
        "error CS", "error TS", "error NU", "error MSB", "ERROR in", "npm ERR!", ": error", "error:"
    ];

    public static string Extract(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "(không có output)";

        var lines = output.Replace("\r\n", "\n").Split('\n');

        var errors = lines
            .Where(l => ErrorMarkers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxErrorLines)
            .ToList();

        var kept = errors.Count > 0
            ? errors
            : lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(TailLines).ToList();

        var text = string.Join(Environment.NewLine, kept).Trim();
        if (text.Length > MaxChars)
            text = text[..MaxChars] + Environment.NewLine + "…[cắt bớt]";

        return text.Length == 0 ? "(không có output)" : text;
    }
}
