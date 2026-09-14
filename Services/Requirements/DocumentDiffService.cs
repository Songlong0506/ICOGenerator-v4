using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace ICOGenerator.Services.Requirements;

public enum DiffLineKind
{
    Same,
    Added,
    Removed
}

public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>
/// Diff hai bản text theo DÒNG cho màn hình lịch sử tài liệu và Prompt Studio — thuần in-memory.
/// <para>
/// Chạy trên DiffPlex thay vì bảng LCS tự viết. Lý do không phải "bớt code" mà là bỏ được một đường lùi
/// XẤU: bản cũ dựng bảng quy hoạch động O(n·m) nên phải tự đặt trần <c>MaxLcsCells</c>, và vùng đổi vượt
/// trần thì rơi về diff thô "thay cả khối" (toàn bộ cũ Removed + toàn bộ mới Added). Nghĩa là tài liệu
/// càng bất thường — đúng lúc người ta mở diff ra vì thấy lạ — thì diff càng vô dụng. DiffPlex không có
/// trần đó nên mọi tài liệu đều được diff mịn như nhau.
/// </para>
/// <para>
/// <c>ignoreWhiteSpace: false</c> là BẮT BUỘC và là mặc định NGƯỢC với DiffPlex: so khớp phải đúng từng
/// ký tự (như <c>StringComparison.Ordinal</c> của bản cũ), nếu không một dòng bị thụt lề khác đi sẽ hiện
/// là "không đổi" — mà ở tài liệu Markdown thì thay đổi thụt lề đổi luôn cấu trúc danh sách.
/// </para>
/// </summary>
public class DocumentDiffService
{
    public IReadOnlyList<DiffLine> Diff(string? oldText, string? newText)
    {
        var model = InlineDiffBuilder.Diff(
            Normalize(oldText),
            Normalize(newText),
            ignoreWhiteSpace: false,
            ignoreCase: false);

        var result = new List<DiffLine>(model.Lines.Count);
        foreach (var line in model.Lines)
        {
            // Imaginary là dòng đệm canh cột của chế độ side-by-side; đường inline không sinh ra nó, và
            // nếu có thì nó KHÔNG phải nội dung thật nên không được phát ra.
            if (line.Type == ChangeType.Imaginary)
                continue;

            result.Add(new DiffLine(KindOf(line.Type), line.Text ?? string.Empty));
        }

        return result;
    }

    private static DiffLineKind KindOf(ChangeType type) => type switch
    {
        ChangeType.Inserted => DiffLineKind.Added,
        ChangeType.Deleted => DiffLineKind.Removed,
        ChangeType.Unchanged => DiffLineKind.Same,
        // Modified chỉ xuất hiện ở chế độ side-by-side (một dòng đổi vài từ). Đường inline tách nó thành
        // cặp Deleted/Inserted, nên nhánh này là phòng xa: coi như dòng mới chứ không phải "không đổi".
        _ => DiffLineKind.Added
    };

    // Chuẩn hóa xuống dòng TRƯỚC khi diff: bản Windows và bản Linux của cùng một tài liệu không được hiện
    // là "đổi sạch mọi dòng".
    private static string Normalize(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r\n", "\n");
}
