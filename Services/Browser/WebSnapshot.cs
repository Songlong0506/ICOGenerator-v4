namespace ICOGenerator.Services.Browser;

/// <summary>Một điều khiển bấm/gõ được trên trang, đã bóc khỏi DOM.</summary>
/// <param name="Ref">Số thứ tự model dùng để trỏ tới nó (gắn vào DOM bằng <c>data-ico-ref</c>).</param>
/// <param name="Kind">Loại đọc được cho người: link, button, textbox, select, checkbox…</param>
/// <param name="Label">Nhãn hiển thị (hoặc aria-label/placeholder/title khi nhãn rỗng).</param>
/// <param name="Value">Giá trị đang có của ô nhập; rỗng với link/button.</param>
public sealed record WebControl(int Ref, string Kind, string Label, string Value);

/// <summary>
/// Dựng bản đồ điều khiển ĐÁNH SỐ mà model dùng làm địa chỉ: nó đọc <c>[3] button "Tìm kiếm"</c> rồi
/// gọi <c>ClickControl(3)</c>.
///
/// <para>
/// Vì sao đánh số chứ không để model tự viết CSS selector: model không nhìn thấy DOM, nên mọi selector
/// nó viết đều là phỏng đoán — mỗi lần trượt tốn một vòng gọi model và một dòng lỗi trong ngữ cảnh, mà
/// ngân sách bước thì hữu hạn. Đổ cả HTML vào ngữ cảnh để nó tự soi thì phá trần token ngay ở trang
/// đầu tiên. Bản đồ đánh số vừa LÀ ngữ cảnh vừa LÀ địa chỉ, trong một chuỗi ngắn. Repo đã đi hướng này
/// một lần ở <see cref="Artifacts.PlaywrightPocRuntimeChecker"/> (tìm điều khiển theo nhãn chữ); đánh
/// số là bản chặt hơn của cùng ý tưởng — số do máy gán nên không có chuyện khớp nhầm nhãn.
/// </para>
/// </summary>
public static class WebSnapshot
{
    /// <summary>Nhãn dài hơn mức này bị cắt — một nút có nguyên đoạn văn bên trong là chuyện thường.</summary>
    public const int MaxLabelChars = 80;

    /// <summary>
    /// Dựng khối chữ gửi cho model: một dòng tiêu đề (URL + title) rồi mỗi điều khiển một dòng.
    /// Cắt theo <paramref name="maxControls"/> và nói thẳng đã cắt bao nhiêu — im lặng cắt bớt sẽ khiến
    /// model kết luận "trang không có nút đó" trong khi nút nằm ngay dưới trần.
    /// </summary>
    public static string Render(string url, string title, IReadOnlyList<WebControl> controls, int maxControls)
    {
        var lines = new List<string>
        {
            $"URL: {url}",
            $"Tiêu đề: {Clip(title, 120)}"
        };

        if (controls.Count == 0)
        {
            lines.Add("(trang không có điều khiển nào bấm/gõ được — dùng ReadPage để đọc nội dung)");
            return string.Join("\n", lines);
        }

        var cap = Math.Max(1, maxControls);
        lines.Add("Điều khiển (trỏ bằng SỐ trong ngoặc vuông):");
        foreach (var c in controls.Take(cap))
            lines.Add(Format(c));

        if (controls.Count > cap)
            lines.Add($"(còn {controls.Count - cap} điều khiển nữa bị cắt — cuộn trang hoặc thu hẹp bằng ReadPage)");

        return string.Join("\n", lines);
    }

    private static string Format(WebControl c)
    {
        var label = Clip(Flatten(c.Label), MaxLabelChars);
        var line = label.Length == 0
            ? $"[{c.Ref}] {c.Kind}"
            : $"[{c.Ref}] {c.Kind} \"{label}\"";

        var value = Clip(Flatten(c.Value), MaxLabelChars);
        return value.Length == 0 ? line : $"{line} = \"{value}\"";
    }

    /// <summary>
    /// Nhãn nhiều dòng làm vỡ định dạng "một điều khiển một dòng" — và model sẽ đọc phần đuôi như một
    /// điều khiển riêng không có số.
    /// </summary>
    private static string Flatten(string? text) =>
        string.Join(" ", (text ?? string.Empty).Split(['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Trim();

    private static string Clip(string? text, int max)
    {
        text = (text ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>
    /// Cắt văn bản trang theo trần, GIỮ ĐẦU VÀ CUỐI. Cắt cụt đuôi là cách chắc chắn đánh rơi phần kết
    /// quả (bảng giá, tổng cộng) vốn hay nằm cuối trang.
    /// </summary>
    public static string ClipPageText(string text, int maxChars)
    {
        text = text.Trim();
        var cap = Math.Max(200, maxChars);
        if (text.Length <= cap)
            return text;

        var head = cap * 2 / 3;
        var tail = cap - head;
        return text[..head]
               + $"\n\n… (cắt bớt {text.Length - cap} ký tự ở giữa — dùng ReadPage với từ khoá để xem đúng đoạn cần) …\n\n"
               + text[^tail..];
    }
}
