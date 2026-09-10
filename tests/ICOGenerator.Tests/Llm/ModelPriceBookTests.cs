using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// ModelPriceBook là bước "tra đơn giá theo ModelId" mà trang Usage, bảng chất lượng và BudgetGuard cùng
// dùng. Ba nơi đó phải ra CÙNG một con số, nên các quy tắc dưới đây (gộp trùng ModelId, model lạ = 0,
// so khớp không phân biệt hoa thường) phải được chốt ở một chỗ thay vì kiểm gián tiếp qua từng màn hình.
public class ModelPriceBookTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ModelPriceBookTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    // createdAt: chỉ các test về bản ghi TRÙNG ModelId mới cần đặt — ở đó "bản đầu" phải là một mốc có
    // thật trong dữ liệu chứ không phải thứ tự chèn (xem test gộp trùng bên dưới).
    private static AiModel Model(string modelId, decimal input, decimal output, decimal cached = 0m, DateTime? createdAt = null) => new()
    {
        ModelId = modelId,
        Endpoint = "http://localhost",
        ApiKey = "",
        InputPricePerMillionTokens = input,
        CachedInputPricePerMillionTokens = cached,
        OutputPricePerMillionTokens = output,
        CreatedAt = createdAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private async Task<ModelPriceBook> LoadAsync(params AiModel[] models)
    {
        await using (var db = NewDb())
        {
            db.AiModels.AddRange(models);
            await db.SaveChangesAsync();
        }

        await using var read = NewDb();
        return await ModelPriceBook.LoadAsync(read);
    }

    [Fact]
    public async Task CostFor_quy_token_ra_usd_dung_bang_LlmCost()
    {
        var prices = await LoadAsync(Model("gpt-x", input: 10m, output: 30m));

        // 100k input + 100k output = $1 + $3.
        Assert.Equal(4m, prices.CostFor("gpt-x", 100_000, 0, 100_000));
        Assert.Equal(LlmCost.Usd(100_000, 0, 100_000, new LlmPrice(10m, 0m, 30m)), prices.CostFor("gpt-x", 100_000, 0, 100_000));
    }

    [Fact]
    public async Task Model_khong_co_trong_bang_gia_thi_khong_tinh_tien()
    {
        var prices = await LoadAsync(Model("gpt-x", 10m, 30m));

        Assert.Equal(0m, prices.CostFor("model-da-xoa", 1_000_000, 0, 1_000_000));
        Assert.Equal(0m, prices.CostFor(null, 1_000_000, 0, 1_000_000));
        Assert.False(prices.HasPrice("model-da-xoa"));
        Assert.False(prices.HasPrice(null));
    }

    [Fact]
    public async Task Tra_gia_khong_phan_biet_hoa_thuong()
    {
        var prices = await LoadAsync(Model("GPT-X", 10m, 30m));

        Assert.Equal(4m, prices.CostFor("gpt-x", 100_000, 0, 100_000));
        Assert.True(prices.HasPrice("gpt-x"));
    }

    // Hai bản ghi AiModel khác endpoint nhưng cùng ModelId: dictionary không được ném lỗi trùng khóa, VÀ
    // đơn giá chọn ra phải TẤT ĐỊNH — bản khai trước (CreatedAt sớm nhất) thắng.
    //
    // Bản trước của test này chỉ chèn hai model rồi kỳ vọng "bản đầu" là bản truyền vào trước, trong khi
    // cả truy vấn lẫn đường chèn của EF đều không hứa điều đó (không ORDER BY; EF chèn theo khóa chính
    // Guid ngẫu nhiên) — nên nó đỏ ngẫu nhiên khoảng một nửa số lần chạy. Nay dữ liệu tự nói ai là "bản
    // đầu" bằng CreatedAt, và thứ tự chèn bị đảo NGƯỢC lại có chủ ý để không ai vô tình dựa vào nó nữa.
    [Fact]
    public async Task Cung_ModelId_o_nhieu_endpoint_thi_gop_lai_lay_ban_khai_truoc()
    {
        var som = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var muon = som.AddDays(1);

        var prices = await LoadAsync(
            Model("gpt-x", 99m, 99m, createdAt: muon),
            Model("gpt-x", 10m, 30m, createdAt: som));

        Assert.Equal(4m, prices.CostFor("gpt-x", 100_000, 0, 100_000));
        Assert.Equal(10m, prices.PriceFor("gpt-x").Input);
    }

    [Fact]
    public async Task HasPrice_va_HasAnyPricing_coi_gia_0_la_chua_khai_gia()
    {
        var free = await LoadAsync(Model("self-hosted", 0m, 0m));
        Assert.False(free.HasPrice("self-hosted"));
        Assert.False(free.HasAnyPricing);

        var priced = await LoadAsync(Model("self-hosted", 0m, 0m), Model("gpt-x", 10m, 30m));
        Assert.True(priced.HasAnyPricing);
    }

    [Fact]
    public async Task PriceFor_tra_don_gia_de_hien_thi_va_default_khi_khong_co()
    {
        var prices = await LoadAsync(Model("gpt-x", 10m, 30m, cached: 2m));

        var price = prices.PriceFor("gpt-x");
        Assert.Equal(10m, price.Input);
        Assert.Equal(2m, price.CachedInput);
        Assert.Equal(30m, price.Output);

        Assert.Equal(default, prices.PriceFor("khong-ton-tai"));
    }

    public void Dispose() => _connection.Dispose();
}
