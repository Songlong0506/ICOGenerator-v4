using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Đường nạp dữ liệu cho bản xuất hội thoại. Hai điều dễ hỏng mà không ai thấy: (1) các lượt đã bị
// "New Chat" LƯU TRỮ nằm sau global query filter — đếm chúng mà quên IgnoreQueryFilters thì bản xuất
// im lặng khẳng định hội thoại này là tất cả những gì đã diễn ra; (2) tên file đi qua header
// Content-Disposition nên tên dự án tiếng Việt phải được bỏ dấu, không phải thoát bằng dấu hỏi.
public class ExportChatTranscriptQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ExportChatTranscriptQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    // Hai khối hằng số (ranh giới phạm vi, nền tảng đã chốt) đi kèm MỌI lời gọi BA kể cả khi OrgUnits còn
    // trống — nên chúng phải có mặt trong bản xuất kể cả ở dự án chưa gắn đơn vị. Đây là phần từng bị bỏ
    // sót: người chấm không thấy chúng thì đọc mọi dữ kiện đến từ đây ("nhà máy Đồng Nai", "chỉ có email")
    // thành BỊA THÊM, và báo là lỗi NẶNG đúng vào chỗ hệ thống đang chạy đúng.
    [Fact]
    public async Task ExecuteAsync_CarriesTheOrganizationContextGivenToEveryBaCall()
    {
        var projectId = await SeedAsync();

        await using var db = NewDb();
        var export = await NewSut(db).ExecuteAsync(projectId);

        Assert.NotNull(export);
        Assert.Contains("## Phụ lục B. Bối cảnh tổ chức", export!.Content, StringComparison.Ordinal);
        Assert.Contains("RANH GIỚI PHẠM VI: nhà máy Đồng Nai", export.Content, StringComparison.Ordinal);
        Assert.Contains("NỀN TẢNG ĐÃ CHỐT: chỉ có email", export.Content, StringComparison.Ordinal);

        // Khối comment HTML là ghi chú cho người sửa file, model không thấy nó — bản xuất phải chở đúng
        // chuỗi model nhận, không phải nguyên văn file template.
        Assert.DoesNotContain("ghi chú cho người sửa file", export.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ExportsCurrentConversation_AndOnlyCountsArchivedTurns()
    {
        var projectId = await SeedAsync();

        await using var db = NewDb();
        var export = await NewSut(db).ExecuteAsync(projectId);

        Assert.NotNull(export);
        Assert.Contains("Tụi em đang xếp lớp bằng Excel", export!.Content, StringComparison.Ordinal);
        // Lượt của hội thoại CŨ không được đem đi chấm — BA cũng không còn dùng chúng làm ngữ cảnh…
        Assert.DoesNotContain("Câu chuyện của hội thoại cũ", export.Content, StringComparison.Ordinal);
        // …nhưng sự tồn tại của chúng thì phải nói ra.
        Assert.Contains("1 lượt của các hội thoại CŨ", export.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesSourceFilesAndTheRunningPrompts()
    {
        var projectId = await SeedAsync();

        await using var db = NewDb();
        var export = await NewSut(db).ExecuteAsync(projectId);

        Assert.NotNull(export);
        Assert.Contains("khoa-hoc.xlsx", export!.Content, StringComparison.Ordinal);
        Assert.Contains("CHỈ DẪN CHẤM", export.Content, StringComparison.Ordinal);
        Assert.Contains("PROMPT BA", export.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_BuildsAsciiFileNameFromProjectName()
    {
        var projectId = await SeedAsync();

        await using var db = NewDb();
        var export = await NewSut(db).ExecuteAsync(projectId);

        Assert.NotNull(export);
        Assert.StartsWith("chat-ba_quan-ly-dao-tao_", export!.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".md", export.FileName, StringComparison.Ordinal);
        Assert.All(export.FileName, ch => Assert.True(char.IsAscii(ch), $"Tên file còn ký tự ngoài ASCII: {export.FileName}"));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownProject_ReturnsNull()
    {
        await using var db = NewDb();
        Assert.Null(await NewSut(db).ExecuteAsync(Guid.NewGuid()));
    }

    private async Task<Guid> SeedAsync()
    {
        await using var db = NewDb();

        var model = new AiModel { ModelId = "gpt-4o-mini", Endpoint = "https://example.invalid", ApiKey = "x" };
        var agent = new Agent { RoleKey = AgentRoleKey.BusinessAnalyst, Description = "BA", AiModel = model };
        var project = new Project { Name = "Quản lý đào tạo", CreatedByUsername = "user" };

        db.AiModels.Add(model);
        db.Agents.Add(agent);
        db.Projects.Add(project);
        db.AppUsers.Add(new AppUser { Username = "user", DisplayName = "Người dùng", UserMemory = "Là HR." });
        db.AgentConversations.AddRange(
            new AgentConversation
            {
                ProjectId = project.Id,
                AgentId = agent.Id,
                Role = "user",
                Message = "Tụi em đang xếp lớp bằng Excel",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5)
            },
            new AgentConversation
            {
                ProjectId = project.Id,
                AgentId = agent.Id,
                Role = "assistant",
                Message = "Câu chuyện của hội thoại cũ",
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
                ArchivedAt = DateTime.UtcNow.AddMinutes(-20)
            });
        db.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            ProjectId = project.Id,
            FileName = "khoa-hoc.xlsx",
            Kind = SourceFileKind.Spreadsheet,
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ExtractedText = "#### Thống kê cột"
        });

        await db.SaveChangesAsync();
        return project.Id;
    }

    private ExportChatTranscriptQuery NewSut(AppDbContext db)
    {
        var prompts = new StubPrompts();
        return new ExportChatTranscriptQuery(
            db,
            prompts,
            new OrganizationContextService(db, prompts, new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }

        public override string Get(string relativePath) => relativePath switch
        {
            "Eval/chat-review.v1.md" => "CHỈ DẪN CHẤM",
            "BusinessAnalyst/organization-scope.v1.md" => "<!-- ghi chú cho người sửa file -->\nRANH GIỚI PHẠM VI: nhà máy Đồng Nai",
            "BusinessAnalyst/organization-platform.v1.md" => "<!-- ghi chú cho người sửa file -->\nNỀN TẢNG ĐÃ CHỐT: chỉ có email",
            _ => "PROMPT BA"
        };
    }
}
