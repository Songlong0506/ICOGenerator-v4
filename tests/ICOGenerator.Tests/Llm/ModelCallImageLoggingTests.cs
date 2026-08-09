using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Ảnh gửi cho model trước đây biến mất hoàn toàn khỏi call log: RequestJson chỉ dựng từ phần TEXT của
// message. Hệ quả khi truy lỗi là ba tình huống khác hẳn nhau — model không có vision, ảnh bị cắt vì chạm
// trần, đọc file ảnh lỗi nên bỏ qua — để lại log giống hệt nhau. Các test này khóa hai nửa của lời giải:
// RequestJson phải MÔ TẢ được ảnh (không nhúng base64), và bytes phải lưu ra đĩa tìm lại được.
public class ModelCallImageLoggingTests : IDisposable
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly string _root;

    public ModelCallImageLoggingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ico-calllog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void RequestPreview_DescribesImages_WithoutEmbeddingBytes()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "prompt hệ thống"),
            new(ChatRole.User, new List<AIContent>
            {
                new TextContent("Đây là tài liệu nguồn"),
                new DataContent(OnePixelPng, "image/png") { Name = "tai-lieu.docx › Hình 1" },
            }),
        };

        var json = ModelCallRequestPreview.Build(Model(), messages, new ChatOptions(), 1000, streaming: false);
        using var doc = JsonDocument.Parse(json);
        var messageNodes = doc.RootElement.GetProperty("messages");

        // Message toàn chữ giữ nguyên dạng CHUỖI — UI "Dễ đọc" và mọi log cũ đều dựa vào dạng đó.
        Assert.Equal(JsonValueKind.String, messageNodes[0].GetProperty("content").ValueKind);

        var parts = messageNodes[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, parts.ValueKind);
        var image = parts.EnumerateArray().Single(p => p.GetProperty("type").GetString() == "image");
        Assert.Equal(1, image.GetProperty("index").GetInt32());
        Assert.Equal("tai-lieu.docx › Hình 1", image.GetProperty("name").GetString());
        Assert.Equal("image/png", image.GetProperty("mediaType").GetString());
        Assert.Equal(OnePixelPng.Length, image.GetProperty("bytes").GetInt32());

        // Điểm mấu chốt: KHÔNG nhúng base64. Bảng log là thứ BudgetGuard quét trước mỗi lời gọi model, một
        // lượt 12 screenshot là ~1.3MB base64 mỗi dòng.
        Assert.DoesNotContain(Convert.ToBase64String(OnePixelPng), json, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestPreview_NumbersImagesAcrossTheWholeRequest_NotPerMessage()
    {
        // Số thứ tự trong RequestJson là thứ UI dùng để xin lại đúng tấm ảnh, mà file trên đĩa được đánh số
        // chạy suốt cả request. Đánh số lại theo từng message là bấm xem ảnh này ra ảnh khác.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new List<AIContent> { new DataContent(OnePixelPng, "image/png") }),
            new(ChatRole.User, new List<AIContent> { new DataContent(OnePixelPng, "image/png") }),
        };

        var json = ModelCallRequestPreview.Build(Model(), messages, new ChatOptions(), 1000, streaming: false);
        using var doc = JsonDocument.Parse(json);

        var indexes = doc.RootElement.GetProperty("messages").EnumerateArray()
            .SelectMany(m => m.GetProperty("content").EnumerateArray())
            .Where(p => p.GetProperty("type").GetString() == "image")
            .Select(p => p.GetProperty("index").GetInt32())
            .ToList();

        Assert.Equal(new[] { 1, 2 }, indexes);
    }

    [Fact]
    public void Collector_StopsAtTheByteCap_SoNumberingStaysContiguous()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new List<AIContent>
            {
                new DataContent(OnePixelPng, "image/png"),
                new DataContent(OnePixelPng, "image/png"),
                new DataContent(OnePixelPng, "image/png"),
            }),
        };

        var collected = ModelCallImageCollector.Collect(messages, maxTotalBytes: OnePixelPng.Length * 2);

        // Dừng hẳn khi chạm trần (không nhảy cóc lấy tấm sau) để "N tấm đầu được lưu" luôn đúng.
        Assert.Equal(new[] { 1, 2 }, collected.Select(i => i.Index));
    }

    [Fact]
    public void Store_SavesAndFindsImagesByIndex_AndReportsMissingOnes()
    {
        var store = NewStore();
        var projectId = Guid.NewGuid();
        var logId = Guid.NewGuid();

        store.Save(projectId, "Dự án A", logId, new[]
        {
            new ModelCallImage(1, "a.png", "image/png", OnePixelPng),
            new ModelCallImage(2, "b.jpg", "image/jpeg", OnePixelPng),
        });

        Assert.EndsWith("image-1.png", store.Find(projectId, "Dự án A", logId, 1));
        Assert.EndsWith("image-2.jpeg", store.Find(projectId, "Dự án A", logId, 2));
        // Log cũ / ảnh đã bị xóa: trả null để UI hiện ô "ảnh không còn" thay vì vỡ.
        Assert.Null(store.Find(projectId, "Dự án A", logId, 3));
        Assert.Null(store.Find(projectId, "Dự án A", Guid.NewGuid(), 1));
    }

    [Fact]
    public void Store_WhenDisabled_WritesNothing()
    {
        var store = NewStore(enabled: false);
        var projectId = Guid.NewGuid();
        var logId = Guid.NewGuid();

        store.Save(projectId, "Dự án A", logId, new[] { new ModelCallImage(1, "a.png", "image/png", OnePixelPng) });

        Assert.Null(store.Find(projectId, "Dự án A", logId, 1));
    }

    [Fact]
    public void Store_BadWorkspaceConfig_DoesNotThrow()
    {
        // Lưu ảnh là việc phụ trợ: RootPath thiếu/sai tuyệt đối không được làm hỏng lời gọi model.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var store = new ModelCallImageStore(
            new WorkspacePathResolver(config), config, NullLogger<ModelCallImageStore>.Instance);

        var record = Record.Exception(() =>
            store.Save(Guid.NewGuid(), "Dự án A", Guid.NewGuid(), new[] { new ModelCallImage(1, "a.png", "image/png", OnePixelPng) }));

        Assert.Null(record);
    }

    [Fact]
    public async Task Logger_WritesRequestImagesToDisk_UnderTheProjectWorkspace()
    {
        // Mối nối thật giữa hai nửa: middleware gom ảnh vào LlmCallResult, chỗ ghi log đẩy chúng ra đĩa ở
        // đúng thư mục mà endpoint xem ảnh sẽ tìm tới. Hỏng mối này thì manifest vẫn đẹp mà bấm vào rỗng.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        var projectId = Guid.NewGuid();
        var aiModel = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        var agent = new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = aiModel.Id };
        await using (var seed = new AppDbContext(options, new PassthroughApiKeyProtector()))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AiModels.Add(aiModel);
            seed.Projects.Add(new Project { Id = projectId, Name = "Dự án A", Description = "d" });
            seed.Agents.Add(agent);
            await seed.SaveChangesAsync();
        }

        var store = NewStore();
        var result = new LlmCallResult
        {
            ModelId = "m",
            IsSuccess = true,
            RequestImages = new[] { new ModelCallImage(1, "tai-lieu.docx › Hình 1", "image/png", OnePixelPng) },
        };

        await using (var db = new AppDbContext(options, new PassthroughApiKeyProtector()))
            await new ModelCallLogger(db, store).LogAsync(projectId, agent, result, step: 1, purpose: "BAChat");

        await using var verify = new AppDbContext(options, new PassthroughApiKeyProtector());
        var logId = (await verify.AgentModelCallLogs.SingleAsync()).Id;
        var path = store.Find(projectId, "Dự án A", logId, 1);

        Assert.NotNull(path);
        Assert.Equal(OnePixelPng, await File.ReadAllBytesAsync(path!));
    }

    private ModelCallImageStore NewStore(bool enabled = true)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = _root,
                ["Llm:CallLog:StoreRequestImages"] = enabled ? "true" : "false",
            })
            .Build();
        return new ModelCallImageStore(
            new WorkspacePathResolver(config), config, NullLogger<ModelCallImageStore>.Instance);
    }

    private static AiModel Model() => new() { ModelId = "m", Endpoint = "http://localhost" };

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* dọn dẹp best-effort */ }
    }
}
