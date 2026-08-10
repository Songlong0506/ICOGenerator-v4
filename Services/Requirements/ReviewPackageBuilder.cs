using System.Text;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>Vì sao một phần của gói vắng mặt — im lặng bỏ qua thì người chấm kết luận sai về phần họ không được đưa.</summary>
public enum ReviewPackagePartState
{
    /// <summary>Có nội dung, đã nằm trong gói.</summary>
    Present,

    /// <summary>Hệ thống chưa sinh ra nó (chưa "Write Requirement" / chưa Approve / chưa dựng POC).</summary>
    NotGenerated,

    /// <summary>Có trên hệ thống nhưng người tải KHÔNG có quyền đọc — gói co lại theo quyền, không nới quyền qua đường tải về.</summary>
    NotPermitted
}

/// <param name="VersionLabel">draft / V1 / V2… — bản nào được đem xuất.</param>
/// <param name="CreatedAtUtc">Lúc bản này được sinh; dùng để phát hiện POC dựng từ bản kỹ thuật CŨ.</param>
public record ReviewPackageDoc(
    ReviewPackagePartState State,
    string? Content = null,
    string? VersionLabel = null,
    DateTime? CreatedAtUtc = null);

/// <param name="HistoryRounds">Số vòng dựng đã được chụp lại (xem PocSnapshots) — cho biết bản demo này là vòng thứ mấy.</param>
public record ReviewPackagePoc(
    ReviewPackagePartState State,
    long SizeBytes = 0,
    DateTime? LastWriteUtc = null,
    int HistoryRounds = 0);

/// <summary>Ảnh chụp mọi thứ cần để dựng gói rà soát. Gom thành record để <see cref="ReviewPackageBuilder"/> là hàm THUẦN.</summary>
/// <param name="ReviewBrief">Chỉ dẫn chấm cho AI đọc gói (prompt <c>Eval/delivery-review.v1.md</c>).</param>
/// <param name="ChatTranscript">Nội dung bản xuất hội thoại, do <see cref="ChatExportBuilder"/> dựng.</param>
public record ReviewPackageSnapshot(
    Project Project,
    string ReviewBrief,
    string ChatTranscript,
    ReviewPackageDoc ProductBrief,
    ReviewPackageDoc AiDesignSpec,
    ReviewPackagePoc Poc,
    DateTime ExportedAtUtc);

/// <summary>Một file trong gói .zip.</summary>
public record ReviewPackageFile(string Name, string Content);

/// <summary>
/// Dựng gói rà soát cả CHUỖI DẪN XUẤT (hội thoại → Product Brief → AI Design Spec → POC) để người dùng
/// đem sang một công cụ AI khác (Claude Code, ChatGPT…) nhờ soi.
///
/// <para>
/// Vì sao là .zip nhiều file chứ không phải một .md như bản xuất hội thoại: bản demo là một file HTML
/// vài trăm KB mà phần lớn là khung dùng chung của template — nhét nguyên vào một file Markdown thì thứ
/// AI ít cần nhất lại chiếm hết cửa sổ ngữ cảnh, và bản demo cũng mất luôn khả năng mở bằng trình duyệt.
/// Tách file để công cụ đọc đúng phần nó cần, và <c>04-poc-demo.html</c> vẫn xem trực tiếp được.
/// </para>
///
/// <para>
/// Vì sao có <c>00-README.md</c> riêng: giá trị của gói nằm ở chỗ đặt bốn tầng CẠNH nhau, nên điều đầu
/// tiên người chấm cần biết là bốn tầng này có thuộc cùng một thời điểm không. Một bản mô tả V1 chấm cạnh
/// một POC dựng từ V3 sẽ sinh ra một danh sách "sai lệch" hoàn toàn giả.
/// </para>
///
/// <para>Lớp này chỉ dựng các file VĂN BẢN. Bản demo là file trên đĩa, do use case đọc và nhét vào zip.</para>
/// </summary>
public static class ReviewPackageBuilder
{
    public const string ReadmeEntry = "00-README.md";
    public const string ChatEntry = "01-chat-ba.md";
    public const string ProductBriefEntry = "02-product-brief.md";
    public const string AiDesignSpecEntry = "03-ai-design-spec.md";
    public const string PocEntry = "04-poc-demo.html";

    public static IReadOnlyList<ReviewPackageFile> Build(ReviewPackageSnapshot snapshot)
    {
        var files = new List<ReviewPackageFile>
        {
            new(ReadmeEntry, BuildReadme(snapshot)),
            new(ChatEntry, snapshot.ChatTranscript)
        };

        if (snapshot.ProductBrief is { State: ReviewPackagePartState.Present, Content: { } brief })
            files.Add(new ReviewPackageFile(ProductBriefEntry, BuildDoc(snapshot, "Product Brief", snapshot.ProductBrief, brief, ProductBriefIntro)));

        if (snapshot.AiDesignSpec is { State: ReviewPackagePartState.Present, Content: { } spec })
            files.Add(new ReviewPackageFile(AiDesignSpecEntry, BuildDoc(snapshot, "AI Design Spec", snapshot.AiDesignSpec, spec, AiDesignSpecIntro)));

        return files;
    }

    private const string ProductBriefIntro =
        "Bản mô tả sản phẩm dành cho NGƯỜI DÙNG NGHIỆP VỤ — sinh tự động từ buổi phỏng vấn ở "
        + ChatEntry + ", và là thứ người dùng đọc rồi duyệt. Mọi dữ kiện ở đây phải truy ngược được về "
        + "một câu người dùng thật sự nói trong hội thoại đó.";

    private const string AiDesignSpecIntro =
        "Bản kỹ thuật súc tích dành cho agent dựng bản demo — sinh từ Product Brief đã duyệt, và là đầu vào "
        + "DUY NHẤT để dựng " + PocEntry + ". Ngắn hơn Product Brief là đúng thiết kế (nó chỉ chở phần dựng "
        + "được); chỉ là lỗi khi thứ bị bỏ có ảnh hưởng tới hành vi của sản phẩm.";

    private static string BuildReadme(ReviewPackageSnapshot snapshot)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Gói rà soát dây chuyền — {OneLine(snapshot.Project.Name)}");
        sb.AppendLine();
        sb.AppendLine($"> Xuất từ ICOGenerator lúc {FormatTime(snapshot.ExportedAtUtc)} (giờ máy chủ). "
            + "Gói này chứa nội dung nghiệp vụ nội bộ và prompt hệ thống của agent — cân nhắc trước khi gửi ra ngoài.");
        sb.AppendLine();

        // Chỉ dẫn đứng TRƯỚC dữ liệu, cùng lý do với bản xuất hội thoại: file đầu tiên công cụ AI đọc là
        // thứ định đoạt nó làm gì với phần còn lại của gói.
        sb.AppendLine(snapshot.ReviewBrief.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        AppendContents(sb, snapshot);
        AppendVersions(sb, snapshot);

        return sb.ToString();
    }

    private static void AppendContents(StringBuilder sb, ReviewPackageSnapshot snapshot)
    {
        sb.AppendLine("## Gói này thực sự có gì");
        sb.AppendLine();
        sb.AppendLine("| File | Trạng thái |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| `{ChatEntry}` | có — buổi phỏng vấn, bản đồ bao phủ, tài liệu nguồn, prompt hệ thống của BA |");
        sb.AppendLine($"| `{ProductBriefEntry}` | {DocStatus(snapshot.ProductBrief, "chưa bấm \"Write Requirement\" nên chưa có bản mô tả nào")} |");
        sb.AppendLine($"| `{AiDesignSpecEntry}` | {DocStatus(snapshot.AiDesignSpec, "chưa duyệt yêu cầu nên chưa sinh bản kỹ thuật")} |");
        sb.AppendLine($"| `{PocEntry}` | {PocStatus(snapshot.Poc)} |");
        sb.AppendLine();

        var missing = new List<string>();
        if (snapshot.ProductBrief.State != ReviewPackagePartState.Present) missing.Add(ProductBriefEntry);
        if (snapshot.AiDesignSpec.State != ReviewPackagePartState.Present) missing.Add(AiDesignSpecEntry);
        if (snapshot.Poc.State != ReviewPackagePartState.Present) missing.Add(PocEntry);

        if (missing.Count > 0)
        {
            // Câu này quan trọng hơn vẻ ngoài của nó: thiếu một tầng mà không nói ra thì mọi phát hiện
            // "tầng sau bỏ mất X" đều là kết luận về một file người chấm chưa từng thấy.
            sb.AppendLine($"**Đừng kết luận về phần vắng mặt** ({string.Join(", ", missing.Select(x => $"`{x}`"))}): "
                + "không có file nghĩa là bạn không có dữ kiện, không phải nội dung đó bị thiếu.");
            sb.AppendLine();
        }
    }

    private static void AppendVersions(StringBuilder sb, ReviewPackageSnapshot snapshot)
    {
        sb.AppendLine("## Các tầng này có cùng một thời điểm không");
        sb.AppendLine();
        sb.AppendLine($"- **Product Brief**: {VersionLine(snapshot.ProductBrief)}");
        sb.AppendLine($"- **AI Design Spec**: {VersionLine(snapshot.AiDesignSpec)}");
        sb.AppendLine($"- **POC demo**: {PocVersionLine(snapshot.Poc)}");
        sb.AppendLine();

        var warnings = BuildSyncWarnings(snapshot);
        if (warnings.Count == 0)
        {
            sb.AppendLine("Không phát hiện lệch pha giữa các tầng — cứ chấm bình thường.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("⚠️ **Lệch pha giữa các tầng — đọc trước khi chấm:**");
        sb.AppendLine();
        foreach (var warning in warnings)
            sb.AppendLine($"- {warning}");
        sb.AppendLine();
        sb.AppendLine("Sai lệch sinh ra từ việc lệch pha này **không phải lỗi của dây chuyền** — đừng báo chúng "
            + "như phát hiện. Nếu một khác biệt có thể giải thích bằng lệch pha, hãy nói rõ điều đó thay vì kết luận.");
        sb.AppendLine();
    }

    /// <summary>
    /// Hai phép so tất định, cố tình dè dặt (chỉ cảnh báo khi có bằng chứng rõ ràng) — thà bỏ sót một cảnh
    /// báo còn hơn dạy người chấm bỏ qua các khác biệt thật.
    /// </summary>
    private static List<string> BuildSyncWarnings(ReviewPackageSnapshot snapshot)
    {
        var warnings = new List<string>();

        var brief = snapshot.ProductBrief;
        var spec = snapshot.AiDesignSpec;

        if (brief.State == ReviewPackagePartState.Present
            && spec.State == ReviewPackagePartState.Present
            && !string.Equals(brief.VersionLabel, spec.VersionLabel, StringComparison.OrdinalIgnoreCase))
        {
            // Ca thường gặp nhất: Brief đang là bản NHÁP đang sửa, còn Spec chỉ sinh ở lúc Approve nên vẫn
            // là của phiên bản đã duyệt trước đó. Chúng KHÔNG mô tả cùng một sản phẩm.
            warnings.Add($"Bản mô tả đang xuất là **{VersionText(brief.VersionLabel)}** còn bản kỹ thuật là "
                + $"**{VersionText(spec.VersionLabel)}** — bản kỹ thuật chỉ được sinh lại lúc duyệt yêu cầu, "
                + "nên nó có thể chưa mang các thay đổi mới nhất của bản mô tả.");
        }

        if (spec is { State: ReviewPackagePartState.Present, CreatedAtUtc: { } specCreated }
            && snapshot.Poc is { State: ReviewPackagePartState.Present, LastWriteUtc: { } pocWritten }
            && pocWritten < specCreated)
        {
            warnings.Add($"Bản demo sửa lần cuối lúc {FormatTime(pocWritten)}, TRƯỚC khi bản kỹ thuật hiện tại "
                + $"được sinh ({FormatTime(specCreated)}) — bản demo trong gói dựng từ một bản kỹ thuật cũ hơn.");
        }

        return warnings;
    }

    private static string BuildDoc(
        ReviewPackageSnapshot snapshot,
        string title,
        ReviewPackageDoc doc,
        string content,
        string intro)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {title} — {OneLine(snapshot.Project.Name)}");
        sb.AppendLine();
        sb.AppendLine($"> Phiên bản {VersionText(doc.VersionLabel)}"
            + (doc.CreatedAtUtc is { } created ? $" · sinh lúc {FormatTime(created)}" : "")
            + $" · xuất lúc {FormatTime(snapshot.ExportedAtUtc)}.");
        sb.AppendLine();
        sb.AppendLine(intro);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Nguyên văn, KHÔNG bọc hàng rào code: đây là văn bản Markdown do agent soạn và người dùng đọc ở
        // trang Requirements — bọc lại thì mọi tiêu đề/bảng của nó thành chữ thô, khó đọc cho cả người lẫn máy.
        sb.AppendLine(content.Replace("\r\n", "\n").TrimEnd());
        sb.AppendLine();

        return sb.ToString();
    }

    private static string DocStatus(ReviewPackageDoc doc, string notGeneratedReason) => doc.State switch
    {
        ReviewPackagePartState.Present => $"có — phiên bản {VersionText(doc.VersionLabel)}",
        ReviewPackagePartState.NotPermitted => "**vắng mặt** — người tải gói không có quyền đọc tài liệu này",
        _ => $"**vắng mặt** — {notGeneratedReason}"
    };

    private static string PocStatus(ReviewPackagePoc poc) => poc.State switch
    {
        // Dưới 1 KB thì báo theo byte: một bản demo thật nặng hàng trăm KB, nên dòng "0 KB" của phép chia
        // nguyên đọc như lỗi xuất file chứ không như một bản demo bé.
        ReviewPackagePartState.Present => $"có — {(poc.SizeBytes < 1024 ? $"{poc.SizeBytes} B" : $"{poc.SizeBytes / 1024} KB")}, bản demo chạy được (mở bằng trình duyệt)",
        ReviewPackagePartState.NotPermitted => "**vắng mặt** — người tải gói không có quyền xem bản demo",
        _ => "**vắng mặt** — dự án chưa dựng bản demo nào"
    };

    private static string VersionLine(ReviewPackageDoc doc) => doc.State switch
    {
        ReviewPackagePartState.Present => VersionText(doc.VersionLabel)
            + (doc.CreatedAtUtc is { } created ? $", sinh lúc {FormatTime(created)}" : ""),
        ReviewPackagePartState.NotPermitted => "(không có trong gói — thiếu quyền)",
        _ => "(chưa sinh)"
    };

    private static string PocVersionLine(ReviewPackagePoc poc)
    {
        if (poc.State != ReviewPackagePartState.Present)
            return poc.State == ReviewPackagePartState.NotPermitted ? "(không có trong gói — thiếu quyền)" : "(chưa dựng)";

        var line = poc.LastWriteUtc is { } written ? $"bản hiện tại, sửa lần cuối {FormatTime(written)}" : "bản hiện tại";

        // Số vòng dựng là thứ giải thích vì sao demo có thể chứa dấu vết của các yêu cầu đã bị thay đổi:
        // vòng "Yêu cầu chỉnh sửa" GHI ĐÈ lên bản hiện tại chứ không dựng lại từ đầu.
        return poc.HistoryRounds > 0
            ? $"{line} · đây là kết quả sau {poc.HistoryRounds + 1} vòng dựng"
            : line;
    }

    private static string VersionText(string? versionLabel) =>
        string.IsNullOrWhiteSpace(versionLabel) ? "(không rõ)"
        : versionLabel.Equals("draft", StringComparison.OrdinalIgnoreCase) ? "bản nháp (chưa duyệt)"
        : versionLabel;

    private static string OneLine(string? value, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return string.Join(" ", value.Replace("\r", " ").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())).Trim();
    }

    private static string FormatTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}
