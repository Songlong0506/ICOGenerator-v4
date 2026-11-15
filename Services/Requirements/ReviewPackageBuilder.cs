using ICOGenerator.Localization;
using System.Text;
using ICOGenerator.Domain;
using static ICOGenerator.Services.Requirements.RequirementText;

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

    private static string ProductBriefIntro => Loc.T("ReviewPackageBuilder.ProductBriefIntro", ChatEntry);

    private static string AiDesignSpecIntro => Loc.T("ReviewPackageBuilder.AiDesignSpecIntro", PocEntry);

    private static string BuildReadme(ReviewPackageSnapshot snapshot)
    {
        var sb = new StringBuilder();

        sb.AppendLine(Loc.T("ReviewPackageBuilder.BuildReadme", OneLine(snapshot.Project.Name)));
        sb.AppendLine();
        sb.AppendLine(Loc.T("ReviewPackageBuilder.BuildReadme.2", FormatTime(snapshot.ExportedAtUtc)));
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
        sb.AppendLine(Loc.T("ReviewPackageBuilder.AppendContents"));
        sb.AppendLine();
        sb.AppendLine(Loc.T("ReviewPackageBuilder.AppendContents.2"));
        sb.AppendLine("|---|---|");
        sb.AppendLine(Loc.T("ReviewPackageBuilder.AppendContents.3", ChatEntry));
        sb.AppendLine($"| `{ProductBriefEntry}` | {DocStatus(snapshot.ProductBrief, Loc.T("ReviewPackageBuilder.AppendContents.4"))} |");
        sb.AppendLine($"| `{AiDesignSpecEntry}` | {DocStatus(snapshot.AiDesignSpec, Loc.T("ReviewPackageBuilder.AppendContents.5"))} |");
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
            sb.AppendLine(Loc.T("ReviewPackageBuilder.AppendContents.6", string.Join(", ", missing.Select(x => $"`{x}`"))));
            sb.AppendLine();
        }
    }

    private static void AppendVersions(StringBuilder sb, ReviewPackageSnapshot snapshot)
    {
        sb.AppendLine(Loc.T("ReviewPackageBuilder.AppendVersions"));
        sb.AppendLine();
        sb.AppendLine($"- **Product Brief**: {VersionLine(snapshot.ProductBrief)}");
        sb.AppendLine($"- **AI Design Spec**: {VersionLine(snapshot.AiDesignSpec)}");
        sb.AppendLine($"- **POC demo**: {PocVersionLine(snapshot.Poc)}");
        sb.AppendLine();

        var warnings = BuildSyncWarnings(snapshot);
        if (warnings.Count == 0)
        {
            sb.AppendLine(Loc.T("ReviewPackageBuilder.AppendVersions.2"));
            sb.AppendLine();
            return;
        }

        sb.AppendLine(Loc.T("ReviewPackageBuilder.AppendVersions.3"));
        sb.AppendLine();
        foreach (var warning in warnings)
            sb.AppendLine($"- {warning}");
        sb.AppendLine();
        sb.AppendLine(Loc.T("ReviewPackageBuilder.AppendVersions.4"));
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
            warnings.Add(Loc.T("ReviewPackageBuilder.BuildSyncWarnings", VersionText(brief.VersionLabel), VersionText(spec.VersionLabel)));
        }

        if (spec is { State: ReviewPackagePartState.Present, CreatedAtUtc: { } specCreated }
            && snapshot.Poc is { State: ReviewPackagePartState.Present, LastWriteUtc: { } pocWritten }
            && pocWritten < specCreated)
        {
            warnings.Add(Loc.T("ReviewPackageBuilder.BuildSyncWarnings.2", FormatTime(pocWritten), FormatTime(specCreated)));
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
        sb.AppendLine(Loc.T("ReviewPackageBuilder.BuildDoc.2", VersionText(doc.VersionLabel), (doc.CreatedAtUtc is { } created ? Loc.T("ReviewPackageBuilder.BuildDoc", FormatTime(created)) : ""), FormatTime(snapshot.ExportedAtUtc)));
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
        ReviewPackagePartState.Present => Loc.T("ReviewPackageBuilder.DocStatus", VersionText(doc.VersionLabel)),
        ReviewPackagePartState.NotPermitted => Loc.T("ReviewPackageBuilder.DocStatus.2"),
        _ => Loc.T("ReviewPackageBuilder.DocStatus.3", notGeneratedReason)
    };

    private static string PocStatus(ReviewPackagePoc poc) => poc.State switch
    {
        // Dưới 1 KB thì báo theo byte: một bản demo thật nặng hàng trăm KB, nên dòng "0 KB" của phép chia
        // nguyên đọc như lỗi xuất file chứ không như một bản demo bé.
        ReviewPackagePartState.Present => Loc.T("ReviewPackageBuilder.PocStatus", (poc.SizeBytes < 1024 ? $"{poc.SizeBytes} B" : $"{poc.SizeBytes / 1024} KB")),
        ReviewPackagePartState.NotPermitted => Loc.T("ReviewPackageBuilder.PocStatus.2"),
        _ => Loc.T("ReviewPackageBuilder.PocStatus.3")
    };

    private static string VersionLine(ReviewPackageDoc doc) => doc.State switch
    {
        ReviewPackagePartState.Present => VersionText(doc.VersionLabel)
            + (doc.CreatedAtUtc is { } created ? Loc.T("ReviewPackageBuilder.VersionLine", FormatTime(created)) : ""),
        ReviewPackagePartState.NotPermitted => Loc.T("ReviewPackageBuilder.VersionLine.2"),
        _ => Loc.T("ReviewPackageBuilder.VersionLine.3")
    };

    private static string PocVersionLine(ReviewPackagePoc poc)
    {
        if (poc.State != ReviewPackagePartState.Present)
            return poc.State == ReviewPackagePartState.NotPermitted ? Loc.T("ReviewPackageBuilder.PocVersionLine") : Loc.T("ReviewPackageBuilder.PocVersionLine.2");

        var line = poc.LastWriteUtc is { } written ? Loc.T("ReviewPackageBuilder.PocVersionLine.3", FormatTime(written)) : Loc.T("ReviewPackageBuilder.PocVersionLine.4");

        // Số vòng dựng là thứ giải thích vì sao demo có thể chứa dấu vết của các yêu cầu đã bị thay đổi:
        // vòng "Yêu cầu chỉnh sửa" GHI ĐÈ lên bản hiện tại chứ không dựng lại từ đầu.
        return poc.HistoryRounds > 0
            ? Loc.T("ReviewPackageBuilder.PocVersionLine.5", line, poc.HistoryRounds + 1)
            : line;
    }

    private static string VersionText(string? versionLabel) =>
        string.IsNullOrWhiteSpace(versionLabel) ? Loc.T("ReviewPackageBuilder.VersionText")
        : versionLabel.Equals("draft", StringComparison.OrdinalIgnoreCase) ? Loc.T("ReviewPackageBuilder.VersionText.2")
        : versionLabel;

    private static string FormatTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}
