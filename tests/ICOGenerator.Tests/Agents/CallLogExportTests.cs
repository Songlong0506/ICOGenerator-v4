using ICOGenerator.Application.Agents;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Agents;

// Nút "tải lời gọi model ra file" tồn tại để mang TRỌN ngữ cảnh một lượt gọi đi hỏi chỗ khác: response lệch
// là do prompt hay do context. Hai thứ phá hỏng đúng mục đích đó, và đây là chỗ khóa chúng lại:
//  • nội dung bị cắt hoặc bị khối code của chính prompt đóng sớm — người đọc mất đúng phần cần soi;
//  • cụm lượt gom sai — kéo theo việc của agent khác, hoặc cắt mất bước đã nạp ngữ cảnh cho lời gọi đang xem.
public class CallLogExportTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 13, 5, 0, DateTimeKind.Utc);

    [Fact]
    public void Render_KeepsPromptVerbatim_EvenWhenItContainsCodeFences()
    {
        // Prompt của repo này là file Markdown có sẵn khối ```json bên trong. Rào ba backtick cứng sẽ bị
        // chính nội dung đóng sớm và nửa sau của prompt tràn ra ngoài dạng đã render.
        var prompt = "Trả về JSON:\n```json\n{ \"message\": \"...\" }\n```\nKhông thêm chữ nào khác.";
        var markdown = ModelCallLogMarkdown.Render(Item(
            purpose: "BAChat",
            requestJson: Request(("system", prompt), ("user", "Tôi muốn quản lý đơn nghỉ phép")),
            responseText: "{ \"message\": \"Dạ anh/chị cho em hỏi…\" }"));

        Assert.Contains("````text", markdown);
        Assert.Contains(prompt, markdown);
        Assert.Contains("[1] system", markdown);
        Assert.Contains("[2] user", markdown);
        Assert.Contains("Tôi muốn quản lý đơn nghỉ phép", markdown);
        Assert.Contains("Dạ anh/chị cho em hỏi", markdown);
    }

    [Fact]
    public void Render_ShowsErrorSection_OnlyWhenTheCallFailed()
    {
        var failed = ModelCallLogMarkdown.Render(Item(
            purpose: "BARequirementCoverage",
            requestJson: Request(("system", "luật chắt lọc")),
            responseText: "{\"error\":\"This response_format type is unavailable now\"}",
            error: "HTTP 400: response_format không được hỗ trợ",
            isSuccess: false,
            httpStatusCode: 400));

        Assert.Contains("Error (HTTP 400)", failed);
        Assert.Contains("response_format không được hỗ trợ", failed);

        var ok = ModelCallLogMarkdown.Render(Item("BAChat", Request(("system", "x")), "y"));
        Assert.DoesNotContain("# Lỗi", ok);
    }

    [Fact]
    public void Render_DescribesAttachedImages_WithoutPretendingBytesAreInTheFile()
    {
        // RequestJson chỉ chở phần mô tả ảnh (bytes nằm trên đĩa). Bản xuất phải NÓI RA điều đó, nếu không
        // người đọc file đi tìm một thứ chưa từng được xuất ra.
        var requestJson = """
        {
          "model": "m",
          "messages": [
            { "role": "user", "content": [
                { "type": "text", "text": "Đây là tài liệu nguồn" },
                { "type": "image", "index": 1, "name": "quy-trinh.docx › Hình 1", "mediaType": "image/png", "bytes": 20480 }
            ] }
          ]
        }
        """;

        var markdown = ModelCallLogMarkdown.Render(Item("BAChat", requestJson, "ok"));

        Assert.Contains("quy-trinh.docx › Hình 1", markdown);
        Assert.Contains("20.0 KB", markdown);
        Assert.Contains("bytes không nằm trong file này", markdown);
    }

    [Fact]
    public void Render_DumpsRawRequest_WhenItCannotBeParsed()
    {
        // Log rất cũ hoặc lời gọi chết trước khi dựng được preview: bỏ hẳn mục request thì bản xuất vô dụng
        // đúng lúc cần nhất.
        var markdown = ModelCallLogMarkdown.Render(Item("BAChat", "không-phải-json", "ok"));

        Assert.Contains("không-phải-json", markdown);
        Assert.Contains("đổ nguyên văn", markdown);
    }

    [Fact]
    public async Task ExportCallLogQuery_ReturnsNull_ForUnknownId()
    {
        await using var db = await SeedAsync();
        Assert.Null(await new ExportCallLogQuery(db).ExecuteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ExportCallLogQuery_NamesTheFileByPurposeAndTime()
    {
        var id = Guid.NewGuid();
        await using var db = await SeedAsync(log => log(Call(id, "BAChat", T0.AddSeconds(39), 9000)));

        var file = await new ExportCallLogQuery(db).ExecuteAsync(id);

        Assert.NotNull(file);
        Assert.Equal("call-log-BAChat-20260901-130539.md", file!.FileName);
        Assert.Contains("AI Call Log — BAChat", file.Markdown);
    }

    [Fact]
    public async Task ExportCallLogTurnQuery_GathersTheWholeTurn_AroundTheAnchor()
    {
        // Đúng hình dạng một lượt chat thật: các bước nạp ngữ cảnh chạy trước, lượt trả lời ở giữa, bước
        // chắt lọc hậu kỳ chạy sau. Mốc lưu log là lúc lời gọi KẾT THÚC, nên khoảng nghỉ phải đo từ đó tới
        // lúc lời gọi sau BẮT ĐẦU — đo giữa hai mốc CreatedAt thì chính thời lượng 9 giây của lượt trả lời
        // tự tạo ra một khoảng trống không có thật.
        var coverage = Guid.NewGuid();
        var chat = Guid.NewGuid();
        var outlook = Guid.NewGuid();
        var previousTurn = Guid.NewGuid();

        await using var db = await SeedAsync(log =>
        {
            log(Call(previousTurn, "BAChat", T0.AddSeconds(-120), 8000));
            log(Call(coverage, "BARequirementCoverage", T0.AddSeconds(25), 10000));
            log(Call(chat, "BAChat", T0.AddSeconds(39), 9000));
            log(Call(outlook, "BAInterviewOutlook", T0.AddSeconds(40), 1000));
        });

        var file = await new ExportCallLogTurnQuery(db).ExecuteAsync(chat);

        Assert.NotNull(file);
        Assert.Contains("cụm 3 lời gọi", file!.Markdown);
        Assert.Contains("BARequirementCoverage", file.Markdown);
        Assert.Contains("BAInterviewOutlook", file.Markdown);
        Assert.Contains("← **đang xem**", file.Markdown);
        // Lượt TRƯỚC cách 2 phút — người dùng đọc câu trả lời rồi mới gõ tiếp, đó là ranh giới thật.
        Assert.DoesNotContain(previousTurn.ToString(), file.Markdown);
    }

    [Fact]
    public async Task ExportCallLogTurnQuery_IgnoresConcurrentWorkOfOtherAgentsAndRuns()
    {
        // Pipeline nền chạy SONG SONG với khung chat: lọc theo thời gian đơn thuần sẽ nhét lời gọi của
        // Developer vào giữa một lượt chat BA và làm file xuất ra nói dối về nhân quả.
        var chat = Guid.NewGuid();
        var otherAgent = Guid.NewGuid();
        var inRun = Guid.NewGuid();

        await using var db = await SeedAsync(log =>
        {
            log(Call(chat, "BAChat", T0.AddSeconds(39), 9000));
            log(Call(otherAgent, "AgentRun", T0.AddSeconds(41), 2000, agent: DeveloperAgentId));
            log(Call(inRun, "BAProductBrief", T0.AddSeconds(42), 2000, workflowRunId: Guid.NewGuid()));
        });

        var file = await new ExportCallLogTurnQuery(db).ExecuteAsync(chat);

        Assert.NotNull(file);
        Assert.DoesNotContain(otherAgent.ToString(), file!.Markdown);
        Assert.DoesNotContain(inRun.ToString(), file.Markdown);
    }

    // ---- dựng dữ liệu ----

    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid BaAgentId = Guid.NewGuid();
    private static readonly Guid DeveloperAgentId = Guid.NewGuid();

    private static ModelCallLogExportItem Item(
        string purpose, string requestJson, string responseText,
        string? error = null, bool isSuccess = true, int? httpStatusCode = null) =>
        new(Guid.NewGuid(), T0, "Business Analyst", "deepseek-v4-flash", purpose, 1,
            requestJson, responseText, error, 68276, 0, 577, 68853, 9440, httpStatusCode, isSuccess, null);

    private static string Request(params (string Role, string Content)[] messages)
    {
        var nodes = messages.Select(m =>
            $$"""{ "role": "{{m.Role}}", "content": {{System.Text.Json.JsonSerializer.Serialize(m.Content)}} }""");
        return $$"""{ "model": "m", "messages": [{{string.Join(",", nodes)}}], "temperature": 0.3 }""";
    }

    private static AgentModelCallLog Call(
        Guid id, string purpose, DateTime createdAt, long durationMs,
        Guid? agent = null, Guid? workflowRunId = null) => new()
        {
            Id = id,
            ProjectId = ProjectId,
            AgentId = agent ?? BaAgentId,
            WorkflowRunId = workflowRunId,
            AgentName = "Business Analyst",
            ModelId = "deepseek-v4-flash",
            Purpose = purpose,
            Step = 1,
            RequestJson = Request(("system", "prompt nền của " + purpose)),
            ResponseText = "kết quả của " + purpose,
            IsSuccess = true,
            DurationMs = durationMs,
            CreatedAt = createdAt,
        };

    private static async Task<AppDbContext> SeedAsync(Action<Action<AgentModelCallLog>>? logs = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        var db = new AppDbContext(options, new PassthroughApiKeyProtector());
        await db.Database.EnsureCreatedAsync();

        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "deepseek-v4-flash" };
        db.AiModels.Add(model);
        db.Projects.Add(new Project { Id = ProjectId, Name = "Dự án A", Description = "d" });
        db.Agents.Add(new Agent { Id = BaAgentId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
        db.Agents.Add(new Agent { Id = DeveloperAgentId, RoleKey = AgentRoleKey.Developer, AiModelId = model.Id });
        await db.SaveChangesAsync();

        logs?.Invoke(log => db.AgentModelCallLogs.Add(log));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
