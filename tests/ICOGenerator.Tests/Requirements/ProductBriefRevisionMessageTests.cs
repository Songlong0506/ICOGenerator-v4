using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Organization;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Requirements.Templates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Lời nhắn cuối lượt "Write Requirement" phải đến từ vòng SOẠN, không phải vòng sửa. Vòng tự soát là
// đối thoại giữa các agent (reviewer liệt kê vấn đề — vòng sửa vá), nên assistantMessage của bản sửa kể
// lại chính danh sách đó ("bỏ cụm ...", "dùng đúng thuật ngữ 'orgUnit'") — với người dùng đó là lời tự
// kiểm điểm về một bản nháp họ chưa từng đọc. Test chốt: nội dung TÀI LIỆU lấy bản sửa, LỜI NHẮN giữ
// của bản nháp đầu.
public class ProductBriefRevisionMessageTests : IDisposable
{
    private const string DraftMessage = "Đã tạo/cập nhật bản mô tả sản phẩm (Product Brief) dễ hiểu cho bạn xem & duyệt.";
    private const string RevisionMessage = "Đã cập nhật Product Brief theo các vấn đề được nêu: bỏ cụm 'thay thế hoàn "
                                         + "toàn cách làm thủ công trước đây', dùng đúng thuật ngữ 'orgUnit' cho Manager.";
    private const string DraftContent = "# Ứng dụng quản lý khóa học\n\nBản nháp đầu.";
    private const string RevisedContent = "# Ứng dụng quản lý khóa học\n\nBản đã sửa theo tự soát.";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public ProductBriefRevisionMessageTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app quản lý khóa học" });
        // Lượt cuối mang dấu cổng đã verify ⇒ bước soạn đi thẳng, không lượt distill/readiness nào.
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "assistant",
            Message = "Mình đã đủ thông tin.",
            ReadinessVerified = true,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task GenerateOrUpdateDraft_AfterSelfReview_KeepsDraftMessage_AndUsesRevisedContent()
    {
        var llm = new FakeLlm
        {
            Draft = new BAProductBriefResult { AssistantMessage = DraftMessage, ProductBrief = new ProductBriefDto { Content = DraftContent } },
            Review = new ProductBriefReview { Issues = { "Bỏ cụm 'thay thế hoàn toàn cách làm thủ công trước đây'." } },
            Revision = new BAProductBriefResult { AssistantMessage = RevisionMessage, ProductBrief = new ProductBriefDto { Content = RevisedContent } }
        };

        await using var db = NewDb();
        var outcome = await NewDraftSut(db, llm).GenerateOrUpdateDraftAsync(_projectId);

        Assert.Equal(RequirementDraftOutcome.Generated, outcome);
        Assert.Equal(1, llm.RevisionCalls);

        await using var verify = NewDb();
        var lastTurn = await verify.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "assistant")
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .LastAsync();

        Assert.Equal(DraftMessage, lastTurn.Message);
        Assert.DoesNotContain("orgUnit", lastTurn.Message, StringComparison.OrdinalIgnoreCase);

        // Vòng sửa vẫn có tác dụng — nó chỉ không được nói thay lời nhắn.
        var doc = await verify.ProjectDocuments.SingleAsync(d => d.ProjectId == _projectId && d.VersionName == "draft");
        Assert.Contains("Bản đã sửa theo tự soát", doc.Content);
    }

    // Không có vấn đề nào ⇒ không có vòng sửa, lời nhắn vẫn là của bản nháp (đường cũ, chốt lại để phép
    // chép đè bên trên không bị hiểu nhầm thành "luôn ghi đè bằng một câu cứng").
    [Fact]
    public async Task GenerateOrUpdateDraft_ReviewFindsNothing_KeepsDraftMessage_AndSkipsRevision()
    {
        var llm = new FakeLlm
        {
            Draft = new BAProductBriefResult { AssistantMessage = DraftMessage, ProductBrief = new ProductBriefDto { Content = DraftContent } },
            Review = new ProductBriefReview()
        };

        await using var db = NewDb();
        var outcome = await NewDraftSut(db, llm).GenerateOrUpdateDraftAsync(_projectId);

        Assert.Equal(RequirementDraftOutcome.Generated, outcome);
        Assert.Equal(0, llm.RevisionCalls);

        await using var verify = NewDb();
        var lastTurn = await verify.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "assistant")
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .LastAsync();

        Assert.Equal(DraftMessage, lastTurn.Message);
    }

    private static ProductBriefDraftService NewDraftSut(AppDbContext db, ILlmClient llm)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new StubPrompts();
        var catalog = new ProjectArtifactCatalog();
        var templateService = new RequirementTemplateService(new FakeWebHostEnvironment());
        return new ProductBriefDraftService(
            db,
            llm,
            new RequirementPromptBuilder(),
            new RequirementResponseParser(),
            new RequirementDocumentGenerator(db, templateService, new DocxTemplateWriter(), new WorkspacePathResolver(config), catalog, new FakeArtifactStorage()),
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            catalog,
            new ProductBriefReviewParser(),
            new OrganizationContextService(db, prompts, new OrgChartProvider(db, new MemoryCache(new MemoryCacheOptions())),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts)),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new ConversationMemoryService(db, llm, prompts));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Fake ILlmClient trả kết quả theo Purpose của ba lượt trong một lần bấm: soạn → tự soát → sửa.
    private sealed class FakeLlm : ILlmClient
    {
        public BAProductBriefResult? Draft;
        public ProductBriefReview? Review;
        public BAProductBriefResult? Revision;
        public int RevisionCalls;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "fail-open path in tests" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            object? value = logContext.Purpose switch
            {
                "BAProductBrief" => Draft,
                "BAProductBriefReview" => Review,
                "BAProductBriefRevision" => Revision,
                _ => throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}")
            };

            if (logContext.Purpose == "BAProductBriefRevision")
                RevisionCalls++;

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)value));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }

    private sealed class FakeArtifactStorage : IArtifactStorage
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ico-brief-revision-tests", Guid.NewGuid().ToString("N"));

        public void InitializeProjectWorkspace(string projectKey) { }
        public bool TryRenameProjectWorkspace(string oldProjectKey, string newProjectKey) => true;
        public bool TryCopyProjectWorkspace(string sourceProjectKey, string targetProjectKey, IReadOnlyCollection<string>? onlyTopLevelFolders = null) => true;
        public void TryDeleteProjectWorkspace(string projectKey) { }
        public string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact) => Combine(artifact.FileName);
        public string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact) => Combine(versionName, artifact.FileName);
        public string GetSourceUploadDir(string projectKey) => Combine("sources");

        private string Combine(params string[] parts)
        {
            var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }
}
