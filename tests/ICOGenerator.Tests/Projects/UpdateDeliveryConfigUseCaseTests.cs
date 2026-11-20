using ICOGenerator.Application.Projects;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Projects;

// Popup "Config delivery" giữ cấu hình Git của dự án (thay cho section PullRequest cũ của appsettings).
// Ô token là ô CHỈ-GHI — trang không render lại token đã lưu — nên "để trống" phải nghĩa là GIỮ, không
// phải XOÁ; và token phải nằm trong DB ở dạng mã hóa như AiModel.ApiKey. Dùng AesApiKeyProtector THẬT +
// đọc giá trị raw bằng SQL (cùng cách ConversationEncryptionAtRestTests).
public class UpdateDeliveryConfigUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IApiKeyProtector _protector;
    private readonly Guid _projectId = Guid.NewGuid();

    public UpdateDeliveryConfigUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        // Tắt cache service provider: converter mã hóa capture protector của context dựng model đầu tiên
        // (xem ConversationEncryptionAtRestTests) — không tắt thì model passthrough của test khác thắng.
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .EnableServiceProviderCaching(false)
            .Options;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:ApiKeyKey"] = "unit-test-git-token-key-0123456789" })
            .Build();
        _protector = new AesApiKeyProtector(config, NullLogger<AesApiKeyProtector>.Instance);

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.SaveChanges();
    }

    [Fact]
    public async Task SavesGitSettings_AndStoresTokenEncrypted()
    {
        var result = await Execute(new UpdateDeliveryConfigVm
        {
            GitBaseBranch = " develop ",
            GitUsername = "NUU4HC",
            GitAccessToken = "bbs-secret-token"
        });

        Assert.Equal(UpdateDeliveryConfigResult.Updated, result);

        var raw = ReadRawToken();
        Assert.StartsWith("enc:v1:", raw);
        Assert.DoesNotContain("bbs-secret-token", raw);

        var project = await Load();
        Assert.Equal("develop", project.GitBaseBranch);
        Assert.Equal("NUU4HC", project.GitUsername);
        Assert.Equal("bbs-secret-token", project.GitAccessToken);
    }

    // Lưu lại form (ví dụ chỉ đổi Generation Mode) mà ô token trống thì token cũ phải còn nguyên.
    [Fact]
    public async Task BlankToken_KeepsTheStoredToken()
    {
        await Execute(new UpdateDeliveryConfigVm { GitUsername = "u", GitAccessToken = "first" });

        await Execute(new UpdateDeliveryConfigVm { GitUsername = "u", GitAccessToken = "" , IsUseBoschTemplate = false });

        var project = await Load();
        Assert.Equal("first", project.GitAccessToken);
        Assert.False(project.IsUseBoschTemplate);
    }

    [Fact]
    public async Task ClearFlag_RemovesTheStoredToken()
    {
        await Execute(new UpdateDeliveryConfigVm { GitAccessToken = "first" });

        await Execute(new UpdateDeliveryConfigVm { ClearGitAccessToken = true });

        Assert.Null((await Load()).GitAccessToken);
    }

    // Tên nhánh thành đối số của git: "-x" sẽ bị hiểu là tuỳ chọn. Chặn ở form và KHÔNG lưu gì cả.
    [Theory]
    [InlineData("--upload-pack=evil")]
    [InlineData("feature/../main")]
    [InlineData("nhánh chính")]
    public async Task InvalidBaseBranch_IsRejected_AndNothingIsSaved(string branch)
    {
        var result = await Execute(new UpdateDeliveryConfigVm
        {
            GitBaseBranch = branch,
            BackendGitUrl = "https://git.example.com/app.git"
        });

        Assert.Equal(UpdateDeliveryConfigResult.InvalidBaseBranch, result);
        var project = await Load();
        Assert.Null(project.GitBaseBranch);
        Assert.Null(project.BackendGitUrl);
    }

    private async Task<UpdateDeliveryConfigResult> Execute(UpdateDeliveryConfigVm vm)
    {
        vm.ProjectId = _projectId;
        await using var db = NewDb();
        return await new UpdateDeliveryConfigUseCase(db).ExecuteAsync(vm);
    }

    private async Task<Project> Load()
    {
        await using var db = NewDb();
        return await db.Projects.AsNoTracking().FirstAsync(p => p.Id == _projectId);
    }

    private string ReadRawToken()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT GitAccessToken FROM Projects LIMIT 1";
        return (string)cmd.ExecuteScalar()!;
    }

    private AppDbContext NewDb() => new(_options, _protector);

    public void Dispose() => _connection.Dispose();
}
