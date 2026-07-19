using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Nội dung hội thoại user↔agent phải được mã hóa AT REST (yêu cầu bảo mật: DB dump/DBA không đọc được
// transcript). Các test này dùng AesApiKeyProtector THẬT (không phải passthrough) rồi đọc GIÁ TRỊ RAW của
// cột bằng SQL trực tiếp để chứng minh: (1) đúng là ciphertext "enc:v1:" chứ không phải plaintext; và
// (2) app vẫn round-trip đọc lại nguyên văn. Bao phủ cả AgentConversation lẫn AgentModelCallLog (bảng log
// chứa lại toàn bộ transcript nên nếu để hở thì việc mã hóa là vô nghĩa).
public class ConversationEncryptionAtRestTests : IDisposable
{
    private const string Secret = "unit-test-encryption-key-0123456789";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IApiKeyProtector _protector;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Agent _agent;
    private readonly Project _project;

    public ConversationEncryptionAtRestTests()
    {
        _agent = new Agent { Id = Guid.NewGuid(), AiModelId = _model.Id };
        _project = new Project { Id = Guid.NewGuid(), Name = "P" };

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        // EF cache MODEL toàn cục theo internal service provider; converter mã hóa CAPTURE instance protector
        // của context dựng model ĐẦU TIÊN (xem cảnh báo ở AppDbContext.OnModelCreating). Các test khác dùng
        // PassthroughApiKeyProtector — nếu chúng dựng model trước, model cache sẽ dùng passthrough và test này
        // đọc ra plaintext. Tắt cache service provider ⇒ mỗi context tự dựng model với AesApiKeyProtector THẬT.
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .EnableServiceProviderCaching(false)
            .Options;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:ApiKeyKey"] = Secret })
            .Build();
        _protector = new AesApiKeyProtector(config, NullLogger<AesApiKeyProtector>.Instance);

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(_agent);
        db.Projects.Add(_project);
        db.SaveChanges();
    }

    [Fact]
    public async Task AgentConversation_MessageAndSuggestions_StoredAsCiphertext_ButReadBackVerbatim()
    {
        const string message = "Khách hàng muốn xuất báo cáo doanh thu theo quý — thông tin nhạy cảm.";
        const string suggestions = "[\"Cả hai mục tiêu trên\",\"Chỉ mục tiêu thứ nhất\"]";
        var id = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.AgentConversations.Add(new AgentConversation
            {
                Id = id,
                ProjectId = _project.Id,
                AgentId = _agent.Id,
                Role = "assistant",
                Message = message,
                Suggestions = suggestions,
            });
            await db.SaveChangesAsync();
        }

        var (rawMessage, rawSuggestions) = ReadRaw(
            "SELECT Message, Suggestions FROM AgentConversations LIMIT 1");

        // Ở tầng lưu trữ: ciphertext, KHÔNG chứa plaintext.
        Assert.StartsWith("enc:v1:", rawMessage);
        Assert.DoesNotContain("doanh thu", rawMessage);
        Assert.StartsWith("enc:v1:", rawSuggestions!);
        Assert.DoesNotContain("mục tiêu", rawSuggestions);

        // Ở tầng ứng dụng: đọc lại nguyên văn.
        await using var read = NewDb();
        var loaded = await read.AgentConversations.FirstAsync(c => c.Id == id);
        Assert.Equal(message, loaded.Message);
        Assert.Equal(suggestions, loaded.Suggestions);
    }

    [Fact]
    public async Task AgentModelCallLog_TranscriptFields_StoredAsCiphertext_ButReadBackVerbatim()
    {
        const string requestJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"lương nhân viên bí mật\"}]}";
        const string responseText = "Đã ghi nhận yêu cầu về bảng lương.";
        const string errorMessage = "429 rate limit khi gửi nội dung nhạy cảm";
        var id = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.AgentModelCallLogs.Add(new AgentModelCallLog
            {
                Id = id,
                ProjectId = _project.Id,
                AgentId = _agent.Id,
                RequestJson = requestJson,
                ResponseText = responseText,
                ErrorMessage = errorMessage,
            });
            await db.SaveChangesAsync();
        }

        var raw = ReadRaw3(
            "SELECT RequestJson, ResponseText, ErrorMessage FROM AgentModelCallLogs LIMIT 1");

        Assert.StartsWith("enc:v1:", raw.Item1);
        Assert.DoesNotContain("lương", raw.Item1);
        Assert.StartsWith("enc:v1:", raw.Item2);
        Assert.DoesNotContain("bảng lương", raw.Item2);
        Assert.StartsWith("enc:v1:", raw.Item3!);
        Assert.DoesNotContain("nhạy cảm", raw.Item3);

        await using var read = NewDb();
        var loaded = await read.AgentModelCallLogs.FirstAsync(c => c.Id == id);
        Assert.Equal(requestJson, loaded.RequestJson);
        Assert.Equal(responseText, loaded.ResponseText);
        Assert.Equal(errorMessage, loaded.ErrorMessage);
    }

    [Fact]
    public async Task NullSuggestions_StaysNull()
    {
        var id = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.AgentConversations.Add(new AgentConversation
            {
                Id = id,
                ProjectId = _project.Id,
                AgentId = _agent.Id,
                Role = "user",
                Message = "hi",
                Suggestions = null,
            });
            await db.SaveChangesAsync();
        }

        await using var read = NewDb();
        var loaded = await read.AgentConversations.FirstAsync(c => c.Id == id);
        Assert.Null(loaded.Suggestions);
    }

    private (string, string?) ReadRaw(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private (string, string, string?) ReadRaw3(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private AppDbContext NewDb() => new(_options, _protector);

    public void Dispose() => _connection.Dispose();
}
