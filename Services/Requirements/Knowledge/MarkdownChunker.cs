namespace ICOGenerator.Services.Requirements.Knowledge;

/// <summary>
/// Cắt nội dung tài liệu (markdown-ish text trong <c>ProjectDocument.Content</c>) thành các đoạn
/// vừa-miệng cho truy xuất: chia theo heading markdown, đoạn nào dài quá thì cắt tiếp theo paragraph
/// (gói tham lam tới trần ký tự). Đoạn quá ngắn (vụn tiêu đề, dòng trống) bị bỏ — không đáng truy xuất.
/// </summary>
public static class MarkdownChunker
{
    // Trần một đoạn: đủ chứa một mục nghiệp vụ trọn vẹn mà 3-4 đoạn ghép lại vẫn gọn trong ~4KB ngữ cảnh.
    public const int MaxChunkChars = 1200;
    // Đoạn ngắn hơn ngưỡng này gần như chắc chắn là vụn (heading trơ, dòng phân cách) — bỏ.
    public const int MinChunkChars = 60;

    public static List<(string? Heading, string Text)> Split(string content)
    {
        var chunks = new List<(string? Heading, string Text)>();
        if (string.IsNullOrWhiteSpace(content))
            return chunks;

        foreach (var (heading, body) in SplitByHeadings(content))
        {
            foreach (var piece in PackParagraphs(body))
            {
                if (piece.Length >= MinChunkChars)
                    chunks.Add((heading, piece));
            }
        }

        return chunks;
    }

    // Gom các dòng thành section theo heading markdown (#..######). Phần chữ trước heading đầu tiên
    // thành một section không heading.
    private static IEnumerable<(string? Heading, string Body)> SplitByHeadings(string content)
    {
        string? currentHeading = null;
        var body = new List<string>();

        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                var headingText = trimmed.TrimStart('#').Trim();
                if (headingText.Length > 0)
                {
                    if (body.Count > 0)
                        yield return (currentHeading, string.Join("\n", body).Trim());
                    currentHeading = headingText;
                    body.Clear();
                    continue;
                }
            }
            body.Add(rawLine);
        }

        if (body.Count > 0)
            yield return (currentHeading, string.Join("\n", body).Trim());
    }

    // Gói paragraph (tách theo dòng trống) tham lam tới MaxChunkChars. Một paragraph đơn lẻ vượt trần
    // (bảng/danh sách dài không có dòng trống) thì cắt cứng theo trần — thà mất một câu giữa chừng còn
    // hơn thả một đoạn vài nghìn ký tự vào prompt.
    private static IEnumerable<string> PackParagraphs(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            yield break;

        var paragraphs = body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(SplitOversized);

        var current = new List<string>();
        var currentLength = 0;
        foreach (var paragraph in paragraphs)
        {
            if (currentLength > 0 && currentLength + paragraph.Length + 2 > MaxChunkChars)
            {
                yield return string.Join("\n\n", current);
                current.Clear();
                currentLength = 0;
            }
            current.Add(paragraph);
            currentLength += paragraph.Length + 2;
        }

        if (current.Count > 0)
            yield return string.Join("\n\n", current);
    }

    private static IEnumerable<string> SplitOversized(string paragraph)
    {
        for (var offset = 0; offset < paragraph.Length; offset += MaxChunkChars)
            yield return paragraph.Substring(offset, Math.Min(MaxChunkChars, paragraph.Length - offset));
    }
}
