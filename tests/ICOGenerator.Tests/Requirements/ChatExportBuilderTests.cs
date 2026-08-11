using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bản xuất hội thoại để nhờ một AI khác rà soát buổi phỏng vấn. Điều test này giữ KHÔNG phải là câu chữ
// đẹp của file mà là: mọi thứ khiến bản xuất ĐỦ NGHĨA đều phải có mặt. Các cột phụ của một lượt
// (Questions/ColumnMap/FlowDiagram/Attachments) chở đúng những phần mà Message cố ý KHÔNG chứa — bỏ sót
// một cột là bản xuất trông vẫn bình thường nhưng người chấm mất chính cái để đối chiếu, và họ không có
// cách nào biết là đã mất.
public class ChatExportBuilderTests
{
    private const string CoverageMap = "- [RÕ] Mục tiêu & phạm vi: quản lý lịch lớp học {nguồn: \"tụi em đang xếp lớp bằng Excel\"}\n"
        + "- [CHƯA HỎI] Thông báo / nhắc nhở:";

    private static AgentConversation Turn(string role, string message, DateTime? at = null) => new()
    {
        Role = role,
        Message = message,
        CreatedAt = at ?? new DateTime(2026, 8, 10, 3, 0, 0, DateTimeKind.Utc)
    };

    private static ChatExportSnapshot Snapshot(
        Project? project = null,
        IReadOnlyList<AgentConversation>? turns = null,
        IReadOnlyList<ProjectSourceFile>? sources = null,
        int archivedTurnCount = 0,
        string? userMemory = null,
        string? organizationContext = "## Ranh giới phạm vi\nDUY NHẤT nhà máy Bosch tại Đồng Nai.") =>
        new(
            project ?? new Project { Name = "Quản lý đào tạo", Description = "Xếp lịch lớp học" },
            turns ?? new List<AgentConversation>(),
            archivedTurnCount,
            sources ?? new List<ProjectSourceFile>(),
            OrgUnitNote: null,
            BaAgentDescription: "Chuyên viên phân tích nghiệp vụ",
            BaModelId: "gpt-4o-mini",
            BaModelSupportsVision: true,
            UserMemory: userMemory,
            ReviewBrief: "# Vai trò: Chuyên gia rà soát chất lượng buổi phỏng vấn",
            SystemPrompt: "# Vai trò: Business Analyst\nHỏi mỗi lượt một câu.",
            OrganizationContext: organizationContext,
            ExportedAtUtc: new DateTime(2026, 8, 10, 4, 0, 0, DateTimeKind.Utc));

    // Chỉ dẫn cho AI đọc file phải đứng TRƯỚC dữ liệu: file này sinh ra để được dán vào một cuộc chat,
    // mà ở đó phần đầu là thứ quyết định AI kia làm gì với phần còn lại.
    [Fact]
    public void Build_PutsTheReviewBriefBeforeTheData()
    {
        var markdown = ChatExportBuilder.Build(Snapshot(turns: new[] { Turn("user", "Chào bạn") }));

        Assert.True(markdown.IndexOf("# Vai trò: Chuyên gia rà soát", StringComparison.Ordinal)
            < markdown.IndexOf("## 5. Toàn văn hội thoại", StringComparison.Ordinal));
    }

    // Prompt hệ thống là chuẩn để chấm "BA làm vậy có sai không" — thiếu nó thì AI kia chỉ chấm được theo
    // cảm tính về một cuộc phỏng vấn mà nó không biết luật.
    [Fact]
    public void Build_AttachsTheRunningSystemPrompt()
    {
        var markdown = ChatExportBuilder.Build(Snapshot());

        Assert.Contains("## Phụ lục A. Prompt hệ thống của BA", markdown, StringComparison.Ordinal);
        Assert.Contains("Hỏi mỗi lượt một câu.", markdown, StringComparison.Ordinal);
    }

    // Khối bối cảnh tổ chức đi kèm MỌI lời gọi BA (kể cả bước soạn Product Brief) và là NGUỒN của những dữ
    // kiện không ai nói ra trong transcript: phạm vi nhà máy, kênh thông báo, tên department/HoD. Bỏ nó ra
    // khỏi bản xuất là ca đã xảy ra thật: AI rà soát chấm "nhà máy Bosch Đồng Nai" và "email là kênh duy
    // nhất" trong bản mô tả là BỊA THÊM mức NẶNG — cả hai đều là hằng số của sản phẩm và đều ĐÚNG. Người
    // đọc báo cáo đó sẽ đi "sửa" một dữ kiện đang đúng thành sai.
    [Fact]
    public void Build_AttachsTheOrganizationContextGivenToEveryBaCall()
    {
        var markdown = ChatExportBuilder.Build(Snapshot());

        Assert.Contains("## Phụ lục B. Bối cảnh tổ chức", markdown, StringComparison.Ordinal);
        Assert.Contains("DUY NHẤT nhà máy Bosch tại Đồng Nai.", markdown, StringComparison.Ordinal);
    }

    // Không có khối ngữ cảnh thì phải NÓI là không có: bỏ trắng mục thì người chấm không phân biệt được
    // "dự án này BA chạy trần" với "bản xuất quên chở phần này đi" — mà hai thứ đó dẫn tới hai kết luận
    // trái ngược về cùng một dữ kiện trong tài liệu.
    [Fact]
    public void Build_SaysSoWhenThereIsNoOrganizationContext()
    {
        var markdown = ChatExportBuilder.Build(Snapshot(organizationContext: null));

        Assert.Contains("## Phụ lục B. Bối cảnh tổ chức", markdown, StringComparison.Ordinal);
        Assert.Contains("không dựng được khối ngữ cảnh nào", markdown, StringComparison.Ordinal);
    }

    // Bản đồ bao phủ đi NGUYÊN VĂN (kèm khối {nguồn: …}): đây là thứ hệ thống TIN, và lỗi nặng nhất —
    // một nhóm bị chấm [RÕ] oan — chỉ lộ ra khi đặt chính bằng chứng đó cạnh transcript.
    [Fact]
    public void Build_IncludesCoverageMapVerbatimAndReadinessVerdict()
    {
        var project = new Project { Name = "Quản lý đào tạo", RequirementCoverageMap = CoverageMap };

        var markdown = ChatExportBuilder.Build(Snapshot(project));

        Assert.Contains("{nguồn: \"tụi em đang xếp lớp bằng Excel\"}", markdown, StringComparison.Ordinal);
        Assert.Contains("**Chưa sẵn sàng**", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NumbersEveryTurnAndLabelsWhoSpoke()
    {
        var markdown = ChatExportBuilder.Build(Snapshot(turns: new[]
        {
            Turn("user", "Tụi em cần app xếp lớp"),
            Turn("assistant", "Anh/chị kể giúp lần gần nhất xếp lớp?")
        }));

        Assert.Contains("### Lượt 1 — NGƯỜI DÙNG", markdown, StringComparison.Ordinal);
        Assert.Contains("### Lượt 2 — BA", markdown, StringComparison.Ordinal);
    }

    // Message của lượt hỏi GỘP chỉ là câu dẫn — các câu hỏi thật nằm ở cột Questions. Không in chúng ra
    // thì bản xuất che mất chính các câu hỏi, và câu trả lời gộp ở lượt sau trở thành vô nghĩa.
    [Fact]
    public void Build_RendersBatchQuestionsWithTheirAnswerMode()
    {
        var turn = Turn("assistant", "Mình hỏi thêm vài ý nhé:");
        turn.Questions = JsonSerializer.Serialize(new[]
        {
            new BAChatQuestion
            {
                Group = "Thông báo / nhắc nhở",
                Question = "Ai cần được nhắc trước buổi học?",
                Suggestions = new List<string> { "Học viên", "Giảng viên" },
                MultiSelect = true
            },
            new BAChatQuestion
            {
                Group = "Quy trình hiện tại",
                Question = "Anh/chị kể giúp quy trình hiện tại?",
                OpenEnded = true
            }
        });

        var markdown = ChatExportBuilder.Build(Snapshot(turns: new[] { turn }));

        Assert.Contains("Thẻ hỏi gộp — 2 câu trong một lượt", markdown, StringComparison.Ordinal);
        Assert.Contains("Ai cần được nhắc trước buổi học?", markdown, StringComparison.Ordinal);
        Assert.Contains("**chọn nhiều**", markdown, StringComparison.Ordinal);
        Assert.Contains("**câu MỞ, không chip**", markdown, StringComparison.Ordinal);
    }

    // Chip là thứ người dùng BẤM: một câu trả lời bốn chữ chỉ đọc được khi biết nó được chọn từ đâu, và
    // cờ "chọn nhiều" trên một bộ chip phủ định nhau chính là một lỗi mà bản xuất phải để lộ ra.
    [Fact]
    public void Build_RendersSuggestedAnswersWithSelectionMode()
    {
        var turn = Turn("assistant", "Ai sẽ dùng ứng dụng này?");
        turn.Suggestions = JsonSerializer.Serialize(new[] { "Nhân viên", "Quản lý" });
        turn.SuggestionsMultiSelect = true;

        var markdown = ChatExportBuilder.Build(Snapshot(turns: new[] { turn }));

        Assert.Contains("Đáp án gợi ý đã bày ra** (chọn nhiều): [1] Nhân viên · [2] Quản lý", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RendersColumnMapAndFlowDiagramOfferedInATurn()
    {
        var turn = Turn("assistant", "Mình vừa đọc file của anh/chị.");
        turn.ColumnMap = JsonSerializer.Serialize(new[]
        {
            new SourceColumnNote { FileName = "khoa-hoc.xlsx", Column = "Item Title", Meaning = "Tên khóa học", Used = true },
            new SourceColumnNote { FileName = "khoa-hoc.xlsx", Column = "Revision Number", Meaning = "Số bản ghi của hệ cũ", Used = false }
        });
        turn.FlowDiagram = JsonSerializer.Serialize(new[]
        {
            new FlowStep { Actor = "Nhân viên", Action = "Đăng ký lớp", Outcome = "Chờ duyệt" }
        });

        var markdown = ChatExportBuilder.Build(Snapshot(turns: new[] { turn }));

        Assert.Contains("✓ Item Title", markdown, StringComparison.Ordinal);
        Assert.Contains("✗ Revision Number", markdown, StringComparison.Ordinal);
        Assert.Contains("**Nhân viên**: Đăng ký lớp → Chờ duyệt", markdown, StringComparison.Ordinal);
    }

    // Không nói ra thì lượt BA "đọc file" ngay sau đó trông như BA tự nhiên biết nội dung một tài liệu
    // chưa ai gửi — người chấm sẽ ghi đó là "BA bịa".
    [Fact]
    public void Build_MarksTheTurnWhereTheUserAttachedFiles()
    {
        var turn = Turn("user", "Đây là file của em");
        turn.Attachments = JsonSerializer.Serialize(new[] { new ChatAttachment(Guid.NewGuid(), "khoa-hoc.xlsx", false) });

        var markdown = ChatExportBuilder.Build(Snapshot(turns: new[] { turn }));

        Assert.Contains("Đính kèm ở lượt này**: khoa-hoc.xlsx", markdown, StringComparison.Ordinal);
    }

    // Người chấm thấy hội thoại mở đầu giữa chừng mà không biết có phần trước đó sẽ đổ lỗi nhầm cho BA.
    [Fact]
    public void Build_SaysHowManyTurnsWereArchivedByNewChat()
    {
        var markdown = ChatExportBuilder.Build(Snapshot(archivedTurnCount: 12));

        Assert.Contains("12 lượt của các hội thoại CŨ", markdown, StringComparison.Ordinal);
    }

    // Text bóc từ một file Excel đủ sức nuốt cả bản xuất — cắt, và NÓI RÕ là đã cắt (im lặng thì người
    // chấm kết luận "BA không được cho đọc phần còn lại").
    [Fact]
    public void Build_TruncatesSourceTextAndSaysSo()
    {
        var source = new ProjectSourceFile
        {
            FileName = "khoa-hoc.xlsx",
            Kind = SourceFileKind.Spreadsheet,
            ExtractedText = new string('x', ChatExportBuilder.SourceExcerptChars + 500)
        };

        var markdown = ChatExportBuilder.Build(Snapshot(sources: new[] { source }));

        Assert.Contains($"cắt còn {ChatExportBuilder.SourceExcerptChars} ký tự đầu", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', ChatExportBuilder.SourceExcerptChars + 1), markdown, StringComparison.Ordinal);
    }

    // Nội dung nhúng vào hàng rào ``` hoàn toàn có thể CHỨA ```
    // (text bóc từ tài liệu, bản đồ bao phủ do model sinh). Hàng rào ba dấu sẽ đóng sớm và phần còn lại
    // tràn ra ngoài — bản xuất vẫn tải về được, chỉ là hỏng cấu trúc đúng ở chỗ người chấm cần đọc kỹ nhất.
    [Fact]
    public void Build_KeepsFencedBlocksIntactWhenContentContainsFences()
    {
        var project = new Project
        {
            Name = "Quản lý đào tạo",
            RequirementCoverageMap = "- [RÕ] Mục tiêu: người dùng dán ```đoạn code``` vào mô tả"
        };

        var markdown = ChatExportBuilder.Build(Snapshot(project));

        Assert.Contains("````", markdown, StringComparison.Ordinal);
        Assert.Contains("```đoạn code```", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_IncludesUserMemoryAndDistilledLists()
    {
        var project = new Project
        {
            Name = "Quản lý đào tạo",
            DecisionLog = "- Quản lý duyệt đơn là đủ, không cần HR",
            OpenQuestions = "- Danh sách khóa học đồng bộ tự động hay nhập tay?",
            WorkedExamples = "- 3 lớp × 20 học viên ⇒ cần 2 phòng"
        };

        var markdown = ChatExportBuilder.Build(Snapshot(project, userMemory: "Người dùng là HR, không rành kỹ thuật."));

        Assert.Contains("Quản lý duyệt đơn là đủ, không cần HR", markdown, StringComparison.Ordinal);
        Assert.Contains("Danh sách khóa học đồng bộ tự động hay nhập tay?", markdown, StringComparison.Ordinal);
        Assert.Contains("3 lớp × 20 học viên ⇒ cần 2 phòng", markdown, StringComparison.Ordinal);
        Assert.Contains("Người dùng là HR, không rành kỹ thuật.", markdown, StringComparison.Ordinal);
    }
}
