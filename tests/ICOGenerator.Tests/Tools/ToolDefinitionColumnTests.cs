using System.ComponentModel;
using System.Reflection;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Tools.Registry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Tools;

// ToolDiscoveryService chép nguyên [Description] của từng tool xuống bảng ToolDefinitions lúc KHỞI ĐỘNG.
// Nếu cột hẹp hơn mô tả dài nhất, SQL Server ném "String or binary data would be truncated" và app chết
// ngay khi bật lên — build xanh, test xanh, chỉ có production sập (đã xảy ra với SetPocContent 3067 ký tự
// trên cột nvarchar(3000)). Sqlite trong test KHÔNG ép độ dài nên chạy discovery cũng không bắt được;
// vì vậy test này soi thẳng model EF thay vì soi dữ liệu.
public class ToolDefinitionColumnTests
{
    [Fact]
    public void DescriptionColumn_FitsEveryToolDescription()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = new AppDbContext(options, new PassthroughApiKeyProtector());

        var maxLength = db.Model
            .FindEntityType(typeof(ToolDefinition))!
            .FindProperty(nameof(ToolDefinition.Description))!
            .GetMaxLength();

        // null = nvarchar(max): không có trần thì không có gì để vượt.
        if (maxLength is not { } cap) return;

        var tooLong = ToolDiscoveryService.ToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Select(m => (m, desc: m.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Where(x => x.desc is not null && x.desc.Length > cap)
            .Select(x => $"{x.m.DeclaringType!.Name}.{x.m.Name} ({x.desc!.Length} ký tự)")
            .ToList();

        Assert.True(
            tooLong.Count == 0,
            $"ToolDefinitions.Description giới hạn {cap} ký tự nhưng các tool sau dài hơn — app sẽ chết lúc "
            + $"khởi động: {string.Join(", ", tooLong)}");
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
