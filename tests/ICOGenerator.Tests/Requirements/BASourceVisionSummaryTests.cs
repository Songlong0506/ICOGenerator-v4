using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Organization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ICOGenerator.Tests;

namespace ICOGenerator.Tests.Requirements;

// Ảnh tài liệu nguồn được đính vào MỖI lời gọi model, mà mỗi lượt chat là một request mới ⇒ một cuộc chat
// dài trả tiền upload lại cùng bộ ảnh hàng chục lần. Cơ chế chặn: lượt BA xác nhận tài liệu vừa đọc ảnh vừa
// ghi lại nội dung từng hình thành chữ (VisionSummary); từ đó ảnh ngừng đi, chỉ còn phần chữ. Test này khóa
// cả vòng: ảnh CÓ đi ở lượt đọc, mô tả được cất, và lượt sau KHÔNG còn ảnh mà vẫn còn nội dung.
public class BASourceVisionSummaryTests : IDisposable
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test-vision", SupportsVision = true };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();
    private readonly Guid _sourceId = Guid.NewGuid();
    private readonly string _root;

    public BASourceVisionSummaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ico-vision-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        for (var n = 1; n <= 3; n++)
            File.WriteAllBytes(Path.Combine(_root, $"figure-{n}.png"), OnePixelPng);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "quản lý reference belt" });
        db.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            Id = _sourceId,
            ProjectId = _projectId,
            Kind = SourceFileKind.Document,
            FileName = "technical-document.docx",
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            StoredPath = Path.Combine(_root, "technical-document.docx"),
            ExtractedText = "Testing Reference Belt Document. [Hình 1] [Hình 2] [Hình 3]",
            ScannedPageImageCount = 3,
            IsVisionSource = true,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Acknowledge_SendsImagesOnce_ThenReusesTheWrittenNotesInsteadOfResendingThem()
    {
        var llm = new FakeLlm
        {
            AckReply = new BASourceAckReply
            {
                Message = "Mình đọc được màn hình quản lý belt. Đúng chưa ạ?",
                SourceNotes =
                {
                    new SourceVisionNote
                    {
                        FileName = "technical-document.docx",
                        Note = "[Hình 1] — Màn hình Belt Type Setting: cột Belt Type, Belt Size, nút Add."
                    }
                }
            }
        };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        // Lượt đọc tài liệu: cả 3 hình PHẢI thật sự đi trên dây — đây là lượt duy nhất model được nhìn ảnh.
        Assert.Equal(3, llm.LastImageCount);

        await using (var verify = NewDb())
        {
            var stored = await verify.ProjectSourceFiles.FirstAsync(s => s.Id == _sourceId);
            Assert.Contains("Belt Type Setting", stored.VisionSummary);
        }

        // Lượt kế tiếp: KHÔNG upload lại ảnh nữa, nhưng nội dung các hình vẫn còn (dưới dạng chữ) và model
        // được dặn thẳng là ảnh không gửi lại — để nó dùng phần chữ thay vì tưởng mình đang nhìn ảnh.
        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        Assert.Equal(0, llm.LastImageCount);
        Assert.Contains("Belt Type Setting", llm.LastText);
        Assert.Contains("KHÔNG gửi lại", llm.LastText);
    }

    [Fact]
    public async Task Acknowledge_WithoutNotes_KeepsSendingImages_RatherThanLockingInAnEmptyDescription()
    {
        // Model không trả sourceNotes (structured output tắt, model yếu): thà tốn token gửi lại ảnh còn hơn
        // khóa nguồn lại bằng một mô tả rỗng — mất nội dung là hỏng không cứu được, tốn tiền thì không.
        var llm = new FakeLlm { AckReply = new BASourceAckReply { Message = "Mình đã đọc tài liệu." } };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));
        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        Assert.Equal(3, llm.LastImageCount);
        await using var verify = NewDb();
        Assert.Null((await verify.ProjectSourceFiles.FirstAsync(s => s.Id == _sourceId)).VisionSummary);
    }

    [Fact]
    public async Task Acknowledge_WhenCapCutsImages_DoesNotLockInNotesFromAPartialLook()
    {
        // Chỉ 2/3 hình đi kèm ⇒ mô tả (nếu có) chỉ dựa trên 2 hình. Khóa lại là mất trắng hình thứ 3, nên
        // nguồn phải để nguyên cho lượt sau gửi nốt.
        var llm = new FakeLlm
        {
            AckReply = new BASourceAckReply
            {
                Message = "Mình đọc được một phần.",
                SourceNotes = { new SourceVisionNote { FileName = "technical-document.docx", Note = "[Hình 1] — …" } }
            }
        };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm, maxImagesPerCall: 2).AcknowledgeSourcesAsync(_projectId));

        Assert.Equal(2, llm.LastImageCount);
        // Câu ghi chú phải nói đúng 2/3 chứ không mời model đọc 3 tấm ảnh mà nó chỉ nhận được 2.
        Assert.Contains("kèm 2/3", llm.LastText);

        await using var verify = NewDb();
        Assert.Null((await verify.ProjectSourceFiles.FirstAsync(s => s.Id == _sourceId)).VisionSummary);
    }

    [Fact]
    public async Task Acknowledge_WhenImagesNeverWentOut_DoesNotLockInDescriptionsTheModelCannotHaveSeen()
    {
        // Lời gọi mang ảnh chết ở tầng truyền tải (body base64 quá lớn so với trần của gateway) ⇒ LlmClient
        // gửi lại bản KHÔNG ảnh và bật ImagesDropped. Model trả lời được, nhưng phần "nội dung các hình" nó
        // viết ra là bịa — khóa vào VisionSummary là mất vĩnh viễn đường nhìn lại ảnh.
        var llm = new FakeLlm
        {
            ImagesDropped = true,
            AckReply = new BASourceAckReply
            {
                Message = "Mình đọc được phần chữ.",
                SourceNotes = { new SourceVisionNote { FileName = "technical-document.docx", Note = "[Hình 1] — bịa" } }
            }
        };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        await using var verify = NewDb();
        Assert.Null((await verify.ProjectSourceFiles.FirstAsync(s => s.Id == _sourceId)).VisionSummary);
    }

    private BAChatService NewSut(AppDbContext db, ILlmClient llm, int? maxImagesPerCall = null)
    {
        var settings = new Dictionary<string, string?>();
        if (maxImagesPerCall.HasValue)
            settings["Llm:SourceUpload:MaxImagesPerCall"] = maxImagesPerCall.Value.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var prompts = new StubPrompts();
        return new BAChatService(
            db,
            llm,
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            new BAChatReplyParser(),
            new ConversationMemoryService(db, llm, prompts),
            new UserMemoryService(db, llm, prompts),
            new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts)),
            new OrganizationContextService(db, prompts,
                new OrgChartProvider(db, new MemoryCache(new MemoryCacheOptions())),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new InterviewScopeService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_root, true); } catch { /* dọn dẹp best-effort */ }
    }

    private sealed class FakeLlm : ILlmClient
    {
        public BASourceAckReply AckReply = new() { Message = "Đã đọc." };

        /// <summary>Lượt trả lời được, nhưng phần ảnh đã bị bỏ trên đường đi (xem LlmCallResult.ImagesDropped).</summary>
        public bool ImagesDropped;

        /// <summary>Số ảnh THẬT SỰ đi trên dây ở lời gọi gần nhất — thứ duy nhất chứng minh được ảnh có gửi hay không.</summary>
        public int LastImageCount;
        public string LastText = string.Empty;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            var contents = messages.SelectMany(m => m.Contents).ToList();
            LastImageCount = contents.OfType<DataContent>().Count();
            LastText = string.Concat(contents.OfType<TextContent>().Select(t => t.Text));

            object value = logContext.Purpose switch
            {
                "BASourceAck" => AckReply,
                _ => throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}")
            };
            return Task.FromResult(
                (new LlmCallResult { IsSuccess = true, Content = "{}", ImagesDropped = ImagesDropped }, (T?)value));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
