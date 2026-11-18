namespace TeamExport.Stripping;

/// <summary>Một đoạn comment trong file nguồn, tính theo chỉ số ký tự [Start, End).</summary>
public readonly record struct CommentSpan(int Start, int End);

/// <summary>
/// Xoá các đoạn comment khỏi văn bản mà giữ bố cục gọn: comment chiếm trọn dòng thì bỏ cả dòng, comment
/// cuối dòng thì bỏ luôn khoảng trắng đứng trước, comment giữa dòng thì chỉ bỏ đoạn đó (chèn một dấu cách
/// nếu hai bên dính vào nhau). Dùng chung cho mọi ngôn ngữ: bộ tìm comment của từng ngôn ngữ chỉ cần trả
/// về vị trí, còn chuyện cắt dòng/gộp dòng trống nằm ở đây.
/// </summary>
public static class CommentSpanRemover
{
    public static string Remove(string text, IEnumerable<CommentSpan> spans)
    {
        var ordered = spans
            .Select(s => TrimTrailingNewline(text, s))
            .Where(s => s.End > s.Start)
            .OrderByDescending(s => s.Start)
            .ToList();
        for (var k = 1; k < ordered.Count; k++)
        {
            if (ordered[k].End > ordered[k - 1].Start)
                throw new InvalidOperationException($"Hai comment chồng lên nhau tại vị trí {ordered[k].Start}.");
        }

        var sb = new System.Text.StringBuilder(text);
        // Đi từ cuối lên đầu để vị trí của các comment phía trước không bị xô lệch.
        foreach (var span in ordered)
            RemoveOne(sb, span.Start, span.End);
        return sb.ToString();
    }

    private static CommentSpan TrimTrailingNewline(string text, CommentSpan s)
    {
        var end = s.End;
        while (end > s.Start && (text[end - 1] == '\n' || text[end - 1] == '\r'))
            end--;
        return new CommentSpan(s.Start, end);
    }

    private static void RemoveOne(System.Text.StringBuilder sb, int start, int end)
    {
        var lineStart = start;
        while (lineStart > 0 && sb[lineStart - 1] != '\n')
            lineStart--;
        var lineEnd = end; // vị trí của '\n' kết thúc dòng (hoặc cuối văn bản)
        while (lineEnd < sb.Length && sb[lineEnd] != '\n')
            lineEnd++;

        var blankBefore = IsBlank(sb, lineStart, start);
        var blankAfter = IsBlank(sb, end, lineEnd);

        if (blankBefore && blankAfter)
        {
            RemoveWholeLines(sb, lineStart, lineEnd);
            return;
        }

        if (blankAfter)
        {
            // Comment cuối dòng: bỏ cả khoảng trắng trước nó, giữ nguyên ký tự xuống dòng (\r\n hay \n).
            var from = start;
            while (from > lineStart && (sb[from - 1] == ' ' || sb[from - 1] == '\t'))
                from--;
            var to = end;
            while (to < lineEnd && (sb[to] == ' ' || sb[to] == '\t'))
                to++;
            sb.Remove(from, to - from);
            return;
        }

        // Comment giữa dòng.
        var before = start > 0 ? sb[start - 1] : ' ';
        var after = sb[end];
        sb.Remove(start, end - start);
        if (!char.IsWhiteSpace(before) && !char.IsWhiteSpace(after))
            sb.Insert(start, ' ');
        else if (char.IsWhiteSpace(before) && (after == ' ' || after == '\t') && !blankBefore)
            sb.Remove(start, 1);
        else if (blankBefore)
        {
            // Comment đứng đầu dòng, sau nó là code: bỏ khoảng trắng giữa comment và code để code giữ thụt lề cũ.
            var to = start;
            while (to < sb.Length && (sb[to] == ' ' || sb[to] == '\t'))
                to++;
            sb.Remove(start, to - start);
        }
    }

    /// <summary>Bỏ các dòng [lineStart, lineEnd] (gồm cả '\n' cuối) rồi dọn dòng trống thừa hai bên.</summary>
    private static void RemoveWholeLines(System.Text.StringBuilder sb, int lineStart, int lineEnd)
    {
        var hasNewline = lineEnd < sb.Length;
        var removeTo = hasNewline ? lineEnd + 1 : lineEnd;
        if (!hasNewline && lineStart > 0)
        {
            // Dòng cuối file không có '\n' phía sau: bỏ '\n' của dòng trước thay vào đó.
            lineStart--;
            if (lineStart > 0 && sb[lineStart - 1] == '\r')
                lineStart--;
        }
        sb.Remove(lineStart, removeTo - lineStart);

        // Dòng ngay trước và ngay sau chỗ vừa bỏ.
        var prevEnd = lineStart; // vị trí sau '\n' của dòng trước
        var prevStart = prevEnd;
        if (prevEnd > 0)
        {
            prevStart = prevEnd - 1;
            while (prevStart > 0 && sb[prevStart - 1] != '\n')
                prevStart--;
        }
        var atFileStart = lineStart == 0;
        var prevBlank = atFileStart || IsBlank(sb, prevStart, prevEnd);

        var nextStart = lineStart;
        var nextEnd = nextStart;
        while (nextEnd < sb.Length && sb[nextEnd] != '\n')
            nextEnd++;
        var nextBlank = IsBlank(sb, nextStart, nextEnd);
        var hasNext = nextStart < sb.Length;

        if (hasNext && nextBlank && (prevBlank || EndsWithOpener(sb, prevStart, prevEnd)))
        {
            // Dòng trống liền sau một dòng trống (hoặc sau "{"): thừa.
            sb.Remove(nextStart, Math.Min(sb.Length, nextEnd + 1) - nextStart);
        }
        else if (!atFileStart && prevBlank && hasNext && StartsWithCloser(sb, nextStart, nextEnd))
        {
            // Dòng trống ngay trước "}": thừa.
            sb.Remove(prevStart, prevEnd - prevStart);
        }
    }

    private static bool IsBlank(System.Text.StringBuilder sb, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            var c = sb[i];
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                return false;
        }
        return true;
    }

    private static bool EndsWithOpener(System.Text.StringBuilder sb, int from, int to)
    {
        for (var i = to - 1; i >= from; i--)
        {
            var c = sb[i];
            if (c is ' ' or '\t' or '\r' or '\n')
                continue;
            return c is '{' or '(' or '[';
        }
        return false;
    }

    private static bool StartsWithCloser(System.Text.StringBuilder sb, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            var c = sb[i];
            if (c is ' ' or '\t' or '\r')
                continue;
            return c is '}' or ')' or ']';
        }
        return false;
    }
}
