using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bản Product Brief ĐÃ DUYỆT trong ngữ cảnh soạn Brief. Trước đây khối này KHÔNG hề có trong prompt: sau
// Approve, dòng draft bị đổi tên thành "V{n}" nên tra theo "draft" trả rỗng, và transcript trở thành thứ
// DUY NHẤT chở nội dung V1 sang V2. Nạp lại vào prompt vì đó là bản duy nhất người dùng đã ký — nó vừa
// chống trôi qua các lần soạn lại, vừa là thứ cho phép cắt bớt phần hội thoại trước mốc duyệt.
public class BriefApprovedContextTests
{
    private const string BriefFile = "ProductBrief.docx";

    private static ProjectDocument Doc(string version, string content, bool approved, string fileName = BriefFile) => new()
    {
        VersionName = version,
        Content = content,
        IsApproved = approved,
        FileName = fileName,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void GetLatestApproved_PicksHighestVersion_Numerically()
    {
        var project = new Project { Name = "P" };
        project.Documents.Add(Doc("V9", "bản chín", approved: true));
        project.Documents.Add(Doc("V10", "bản mười", approved: true));

        var approved = ProjectDocumentLookup.GetLatestApproved(project, BriefFile);

        // "V9" > "V10" nếu so chuỗi — phải đọc ra số như ApproveRequirementUseCase làm khi tính bản kế tiếp.
        Assert.NotNull(approved);
        Assert.Equal("V10", approved!.VersionName);
        Assert.Equal("bản mười", approved.Content);
    }

    [Fact]
    public void GetLatestApproved_IgnoresDraft_OtherFiles_AndEmptyContent()
    {
        var project = new Project { Name = "P" };
        project.Documents.Add(Doc("draft", "bản nháp mới hơn", approved: false));
        project.Documents.Add(Doc("V2", "  ", approved: true));
        project.Documents.Add(Doc("V3", "spec chứ không phải brief", approved: true, fileName: "AiDesignSpec.docx"));

        Assert.Null(ProjectDocumentLookup.GetLatestApproved(project, BriefFile));
    }

    [Fact]
    public void BuildProductBrief_IncludesApprovedBrief_AndConversationSummary()
    {
        var prompt = new RequirementPromptBuilder().BuildProductBrief(
            new Project { Name = "P", Description = "D" },
            "Người dùng: thêm màn hình báo cáo",
            currentProductBrief: "",
            organizationContext: "",
            distilledState: "",
            conversationSummary: "Người dùng đã chốt quy trình duyệt hai cấp.",
            approvedBrief: new ProjectDocumentLookup.ApprovedDocument("V1", "Nội dung đã duyệt."));

        Assert.Contains("ĐÃ DUYỆT (V1)", prompt);
        Assert.Contains("Nội dung đã duyệt.", prompt);
        Assert.Contains("Tóm tắt các lượt hội thoại CŨ", prompt);
        Assert.Contains("Người dùng đã chốt quy trình duyệt hai cấp.", prompt);
    }

    [Fact]
    public void BuildProductBrief_WithoutApprovedBriefOrSummary_KeepsOldShape()
    {
        var builder = new RequirementPromptBuilder();
        var project = new Project { Name = "P", Description = "D" };

        var prompt = builder.BuildProductBrief(project, "Người dùng: ý gì đó", currentProductBrief: "");

        Assert.DoesNotContain("ĐÃ DUYỆT", prompt);
        Assert.DoesNotContain("Tóm tắt các lượt hội thoại CŨ", prompt);
    }

    [Fact]
    public void BuildProductBriefReview_TellsReviewer_ApprovedContentIsNotFabricated()
    {
        // Vòng tự soát chấm "tự thêm ngoài hội thoại". Transcript nay có thể đã bị cắt bớt, nên nếu không
        // nói rõ, reviewer sẽ chê chính những đoạn đến từ bản đã duyệt/tóm tắt là bịa — rồi vòng sửa xóa
        // đúng phần người dùng đã ký.
        var prompt = new RequirementPromptBuilder().BuildProductBriefReview(
            new Project { Name = "P", Description = "D" },
            "Người dùng: ý gì đó",
            draftProductBrief: "bản nháp",
            organizationContext: "",
            distilledState: "",
            conversationSummary: "tóm tắt cũ",
            approvedBrief: new ProjectDocumentLookup.ApprovedDocument("V1", "Nội dung đã duyệt."));

        Assert.Contains("do NOT flag it as fabricated", prompt);
        Assert.Contains("DO flag it when the draft silently drops something they state", prompt);
    }
}
