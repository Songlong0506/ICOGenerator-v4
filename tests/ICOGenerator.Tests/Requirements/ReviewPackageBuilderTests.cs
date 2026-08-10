using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Gói rà soát dây chuyền: giá trị của nó nằm ở chỗ đặt bốn tầng CẠNH nhau, nên hai điều phải đúng bằng
// mọi giá. (1) Phần VẮNG MẶT phải được nói rõ kèm lý do — im lặng bỏ một tầng thì mọi kết luận "tầng sau
// bỏ mất X" là kết luận về một file người chấm chưa từng thấy. (2) LỆCH PHA giữa các tầng phải được cảnh
// báo — chấm bản mô tả nháp cạnh bản demo dựng từ phiên bản đã duyệt trước đó chỉ sinh ra sai lệch giả.
public class ReviewPackageBuilderTests
{
    [Fact]
    public void Build_PacksReadmeChatAndBothDocuments()
    {
        var files = ReviewPackageBuilder.Build(Snapshot());

        Assert.Equal(
            new[]
            {
                ReviewPackageBuilder.ReadmeEntry,
                ReviewPackageBuilder.ChatEntry,
                ReviewPackageBuilder.ProductBriefEntry,
                ReviewPackageBuilder.AiDesignSpecEntry
            },
            files.Select(x => x.Name));

        // Bản demo KHÔNG do builder dựng (nó là file trên đĩa) — use case nhét vào zip.
        Assert.DoesNotContain(files, x => x.Name == ReviewPackageBuilder.PocEntry);
    }

    [Fact]
    public void Build_EmbedsTheReviewBriefAheadOfTheData()
    {
        var readme = Find(ReviewPackageBuilder.Build(Snapshot()), ReviewPackageBuilder.ReadmeEntry);

        // Chỉ dẫn phải đứng TRƯỚC bảng liệt kê nội dung: file đầu tiên công cụ AI đọc là thứ định đoạt nó
        // làm gì với phần còn lại của gói.
        var briefIdx = readme.IndexOf("CHỈ DẪN CHẤM CHUỖI", StringComparison.Ordinal);
        var contentsIdx = readme.IndexOf("## Gói này thực sự có gì", StringComparison.Ordinal);

        Assert.True(briefIdx >= 0, "README không nhúng chỉ dẫn chấm.");
        Assert.True(briefIdx < contentsIdx, "Chỉ dẫn chấm phải đứng trước phần dữ liệu.");
    }

    [Fact]
    public void Build_DocumentsCarryTheirVersionAndSourceOfTruth()
    {
        var files = ReviewPackageBuilder.Build(Snapshot());

        var brief = Find(files, ReviewPackageBuilder.ProductBriefEntry);
        Assert.Contains("Phiên bản V2", brief, StringComparison.Ordinal);
        Assert.Contains("Nội dung bản mô tả", brief, StringComparison.Ordinal);
        // Người chấm phải biết truy ngược về đâu, nếu không họ chấm bản mô tả như một tài liệu độc lập.
        Assert.Contains(ReviewPackageBuilder.ChatEntry, brief, StringComparison.Ordinal);

        var spec = Find(files, ReviewPackageBuilder.AiDesignSpecEntry);
        Assert.Contains("Nội dung bản kỹ thuật", spec, StringComparison.Ordinal);
        Assert.Contains(ReviewPackageBuilder.PocEntry, spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MissingDesignSpecBecauseOfPermission_SaysSoInsteadOfGoingQuiet()
    {
        var readme = Find(
            ReviewPackageBuilder.Build(Snapshot(spec: new ReviewPackageDoc(ReviewPackagePartState.NotPermitted))),
            ReviewPackageBuilder.ReadmeEntry);

        Assert.Contains("không có quyền đọc tài liệu này", readme, StringComparison.Ordinal);
        Assert.Contains("Đừng kết luận về phần vắng mặt", readme, StringComparison.Ordinal);
        Assert.Contains(ReviewPackageBuilder.AiDesignSpecEntry, readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MissingDocumentsBecauseNothingGenerated_ExplainWhichStepIsMissing()
    {
        var readme = Find(
            ReviewPackageBuilder.Build(Snapshot(
                brief: new ReviewPackageDoc(ReviewPackagePartState.NotGenerated),
                spec: new ReviewPackageDoc(ReviewPackagePartState.NotGenerated),
                poc: new ReviewPackagePoc(ReviewPackagePartState.NotGenerated))),
            ReviewPackageBuilder.ReadmeEntry);

        Assert.Contains("chưa bấm \"Write Requirement\"", readme, StringComparison.Ordinal);
        Assert.Contains("chưa duyệt yêu cầu", readme, StringComparison.Ordinal);
        Assert.Contains("chưa dựng bản demo nào", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_BriefAndSpecOnDifferentVersions_WarnsAboutThePhaseGap()
    {
        // Ca thường gặp nhất: người dùng đang sửa bản NHÁP, còn bản kỹ thuật vẫn là của V1 đã duyệt.
        var readme = Find(
            ReviewPackageBuilder.Build(Snapshot(
                brief: new ReviewPackageDoc(ReviewPackagePartState.Present, "Nội dung bản mô tả", "draft", Now),
                spec: new ReviewPackageDoc(ReviewPackagePartState.Present, "Nội dung bản kỹ thuật", "V1", Now.AddDays(-1)))),
            ReviewPackageBuilder.ReadmeEntry);

        Assert.Contains("Lệch pha giữa các tầng", readme, StringComparison.Ordinal);
        Assert.Contains("bản nháp (chưa duyệt)", readme, StringComparison.Ordinal);
        Assert.Contains("**V1**", readme, StringComparison.Ordinal);
        Assert.Contains("đừng báo chúng", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_PocOlderThanTheSpec_WarnsItWasBuiltFromAnOlderSpec()
    {
        var readme = Find(
            ReviewPackageBuilder.Build(Snapshot(
                poc: new ReviewPackagePoc(ReviewPackagePartState.Present, 4096, Now.AddDays(-3), 2))),
            ReviewPackageBuilder.ReadmeEntry);

        Assert.Contains("dựng từ một bản kỹ thuật cũ hơn", readme, StringComparison.Ordinal);
        // Vòng "Yêu cầu chỉnh sửa" GHI ĐÈ lên bản hiện tại, nên số vòng là lời giải thích cho các dấu vết
        // của những yêu cầu đã bị thay đổi còn sót trong demo.
        Assert.Contains("sau 3 vòng dựng", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EverythingInPhase_SaysThereIsNothingToWatchOutFor()
    {
        var readme = Find(ReviewPackageBuilder.Build(Snapshot()), ReviewPackageBuilder.ReadmeEntry);

        Assert.DoesNotContain("Lệch pha giữa các tầng", readme, StringComparison.Ordinal);
        Assert.Contains("Không phát hiện lệch pha", readme, StringComparison.Ordinal);
    }

    private static readonly DateTime Now = new(2026, 8, 10, 3, 0, 0, DateTimeKind.Utc);

    private static ReviewPackageSnapshot Snapshot(
        ReviewPackageDoc? brief = null,
        ReviewPackageDoc? spec = null,
        ReviewPackagePoc? poc = null) =>
        new(
            new Project { Name = "Quản lý đào tạo" },
            "# CHỈ DẪN CHẤM CHUỖI",
            "# Bản xuất hội thoại BA",
            brief ?? new ReviewPackageDoc(ReviewPackagePartState.Present, "Nội dung bản mô tả", "V2", Now.AddHours(-2)),
            spec ?? new ReviewPackageDoc(ReviewPackagePartState.Present, "Nội dung bản kỹ thuật", "V2", Now.AddHours(-1)),
            poc ?? new ReviewPackagePoc(ReviewPackagePartState.Present, 4096, Now, 2),
            Now);

    private static string Find(IReadOnlyList<ReviewPackageFile> files, string name) =>
        files.Single(x => x.Name == name).Content;
}
