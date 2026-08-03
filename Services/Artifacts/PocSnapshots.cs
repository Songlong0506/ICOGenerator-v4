using System.Globalization;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Artifacts;

/// <summary>Một vòng dựng POC đã được chụp lại: số vòng, thời điểm chụp, đường dẫn file.</summary>
public sealed record PocSnapshot(int Version, DateTime CapturedAtUtc, string FilePath);

/// <summary>
/// Khác biệt CẤU TRÚC giữa hai vòng dựng POC — màn hình được thêm/bỏ. Chỉ so nhãn màn hình
/// (<c>data-view</c> của các section <c>page-view</c>): đó là đơn vị mà người nghiệm thu nhìn thấy và nói
/// được thành lời, còn so từng dòng HTML thì mọi vòng sửa đều "khác nhau toàn bộ" và vô nghĩa với họ.
/// </summary>
public sealed record PocSnapshotDiff(IReadOnlyList<string> AddedScreens, IReadOnlyList<string> RemovedScreens)
{
    public static readonly PocSnapshotDiff Empty = new(Array.Empty<string>(), Array.Empty<string>());

    public bool HasChanges => AddedScreens.Count > 0 || RemovedScreens.Count > 0;
}

/// <summary>
/// Lịch sử các bản <c>poc-demo.html</c> đã dựng, lưu cạnh nó trong
/// <c>04_Implementation/poc-history/poc-demo.V{n}.html</c>.
///
/// <para>
/// Vì sao cần: mỗi vòng "Yêu cầu chỉnh sửa" GHI ĐÈ thẳng lên poc-demo.html, nên bản trước đó biến mất
/// hoàn toàn. <see cref="PocVerification"/> giữ được lịch sử KẾT QUẢ KIỂM (rule nào pass/fail, hồi quy),
/// nhưng người nghiệm thu ở vòng thứ hai trở đi vẫn không có cách nào mở lại bản họ đã xem để đối chiếu,
/// cũng không biết vòng vừa rồi thêm/bỏ màn hình nào — họ phải rà lại cả bản demo từ đầu và tin vào bản
/// bàn giao bằng chữ của agent. Chụp lại mỗi vòng là cách rẻ nhất để trả lời "vòng này đổi gì" bằng
/// bằng chứng thay vì bằng lời.
/// </para>
/// <para>
/// FAIL-OPEN toàn phần như <see cref="PocVerification"/>: mọi lỗi IO đều bị nuốt — lịch sử là tiện ích
/// cho người review, không bao giờ được làm hỏng lượt dựng POC.
/// </para>
/// </summary>
public static partial class PocSnapshots
{
    /// <summary>Số bản chụp giữ lại — trần revision của một bước là 3 vòng, giữ 10 là dư cho cả các lần Approve lại.</summary>
    private const int MaxSnapshots = 10;

    private const string FolderName = "poc-history";

    public static string GetFolderPath(string workspacePath) =>
        Path.Combine(workspacePath, "04_Implementation", FolderName);

    /// <summary>
    /// Chụp lại <c>poc-demo.html</c> hiện tại thành vòng kế tiếp. Trả về bản chụp vừa tạo, hoặc
    /// <c>null</c> khi không có gì để chụp / gặp lỗi IO.
    /// </summary>
    public static PocSnapshot? TryCapture(string workspacePath, string mockupPath)
    {
        try
        {
            if (!File.Exists(mockupPath))
                return null;

            var folder = GetFolderPath(workspacePath);
            Directory.CreateDirectory(folder);

            var existing = List(workspacePath);
            var version = existing.Count == 0 ? 1 : existing[^1].Version + 1;
            var target = Path.Combine(folder, $"poc-demo.V{version}.html");

            File.Copy(mockupPath, target, overwrite: true);

            Prune(existing, MaxSnapshots - 1);

            return new PocSnapshot(version, File.GetLastWriteTimeUtc(target), target);
        }
        catch
        {
            // Chụp lỗi (đĩa đầy, file đang mở) ⇒ bỏ qua: cùng lắm là mất một mốc lịch sử.
            return null;
        }
    }

    /// <summary>Các bản chụp hiện có, cũ trước mới sau. Không có thư mục/lỗi đọc ⇒ danh sách rỗng.</summary>
    public static IReadOnlyList<PocSnapshot> List(string workspacePath)
    {
        try
        {
            var folder = GetFolderPath(workspacePath);
            if (!Directory.Exists(folder))
                return Array.Empty<PocSnapshot>();

            return Directory.EnumerateFiles(folder, "poc-demo.V*.html")
                .Select(path =>
                {
                    var m = VersionRegex().Match(Path.GetFileName(path));
                    return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                        ? new PocSnapshot(v, File.GetLastWriteTimeUtc(path), path)
                        : null;
                })
                .Where(x => x != null)
                .Select(x => x!)
                .OrderBy(x => x.Version)
                .ToList();
        }
        catch
        {
            return Array.Empty<PocSnapshot>();
        }
    }

    /// <summary>Đường dẫn của một vòng cụ thể, hoặc <c>null</c> khi không có (dùng để phục vụ file cho người xem).</summary>
    public static string? TryGetPath(string workspacePath, int version) =>
        List(workspacePath).FirstOrDefault(x => x.Version == version)?.FilePath;

    /// <summary>
    /// Xoá toàn bộ lịch sử. Gọi khi POC được DỰNG LẠI TỪ ĐẦU (re-seed template ở một lượt PocPreview
    /// không phải revision) — cùng lý do <see cref="PocVerification.Reset"/> tồn tại: các bản chụp của
    /// một POC không còn tồn tại chỉ tạo ra so sánh vô nghĩa.
    /// </summary>
    public static void Reset(string workspacePath)
    {
        try
        {
            var folder = GetFolderPath(workspacePath);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // Không xoá được ⇒ bỏ qua; cùng lắm trang review liệt kê thừa vài vòng của bản POC cũ.
        }
    }

    /// <summary>
    /// So màn hình giữa hai bản POC. Nhãn được chuẩn hoá bằng <see cref="PocSpec.Key"/> để khác biệt
    /// hoa/thường hay khoảng trắng không bị tính thành "thêm rồi bỏ" cùng một màn hình.
    /// </summary>
    public static PocSnapshotDiff Diff(string? previousHtml, string? currentHtml)
    {
        if (string.IsNullOrWhiteSpace(previousHtml) || string.IsNullOrWhiteSpace(currentHtml))
            return PocSnapshotDiff.Empty;

        var before = PocContentScreens.Extract(previousHtml);
        var after = PocContentScreens.Extract(currentHtml);

        var beforeKeys = before.Select(PocSpec.Key).ToHashSet(StringComparer.Ordinal);
        var afterKeys = after.Select(PocSpec.Key).ToHashSet(StringComparer.Ordinal);

        var added = after.Where(x => !beforeKeys.Contains(PocSpec.Key(x))).Distinct().ToList();
        var removed = before.Where(x => !afterKeys.Contains(PocSpec.Key(x))).Distinct().ToList();

        return new PocSnapshotDiff(added, removed);
    }

    /// <summary>Đọc nội dung một bản chụp; lỗi/thiếu file ⇒ <c>null</c>.</summary>
    public static string? TryReadContent(string filePath)
    {
        try
        {
            return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }
        catch
        {
            return null;
        }
    }

    // Giữ lại `keep` bản mới nhất, xoá phần cũ hơn.
    private static void Prune(IReadOnlyList<PocSnapshot> existing, int keep)
    {
        if (existing.Count <= keep)
            return;

        foreach (var stale in existing.Take(existing.Count - keep))
        {
            try
            {
                File.Delete(stale.FilePath);
            }
            catch
            {
                // Xoá lỗi ⇒ bỏ qua: thừa file cũ không ảnh hưởng gì ngoài dung lượng.
            }
        }
    }

    [GeneratedRegex(@"^poc-demo\.V(\d+)\.html$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}
