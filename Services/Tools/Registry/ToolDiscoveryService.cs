using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Tools.Registry;

public partial class ToolDiscoveryService
{
    private readonly AppDbContext _db;
    public ToolDiscoveryService(AppDbContext db) { _db = db; }

    public static Type[] ToolTypes =>
    [
        typeof(WorkspaceTools),
        typeof(CommandTools),
        typeof(GitTools)
    ];

    /// <summary>
    /// Đồng bộ bảng <c>ToolDefinitions</c> theo các method có <c>[Description]</c> trong
    /// <see cref="ToolTypes"/>, và trả về tên các tool XUẤT HIỆN LẦN ĐẦU ở lần chạy này.
    /// <para>
    /// Danh sách trả về là đầu vào của bước cấp tool cho vai ở <c>DbInitializer</c>: một tool mới thêm
    /// vào code sẽ được đồng bộ vào bảng định nghĩa, nhưng trước đây KHÔNG vai nào được cấp nó — phần
    /// gán mặc định chỉ chạy đúng một lần lúc seed agent, nên trên mọi DB đã có sẵn, tool mới nằm đó
    /// không ai gọi được cho tới khi có người vào màn Agents tick tay. "Lần đầu xuất hiện" là mốc duy
    /// nhất an toàn để tự cấp: admin bỏ tick sau đó thì lần chạy sau tool không còn mới nữa nên không
    /// bao giờ bị cấp lại.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> SyncToolDefinitionsAsync()
    {
        // Load every existing definition once and match in memory, rather than a DB round-trip per tool
        // method (one query each). The set is tiny and the (ServiceType, MethodName) pair is unique.
        var existingByKey = (await _db.ToolDefinitions.ToListAsync())
            .ToDictionary(x => (x.ServiceType, x.MethodName));

        var newlyAdded = new List<string>();

        foreach (var type in ToolTypes)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.GetCustomAttribute<DescriptionAttribute>() != null);

            foreach (var method in methods)
            {
                var desc = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
                if (!existingByKey.TryGetValue((type.Name, method.Name), out var existing))
                {
                    _db.ToolDefinitions.Add(new ToolDefinition
                    {
                        Name = method.Name,
                        DisplayName = SplitPascalCase(method.Name),
                        Description = desc,
                        ServiceType = type.Name,
                        MethodName = method.Name,
                        IsActive = true
                    });
                    newlyAdded.Add(method.Name);
                }
                else
                {
                    existing.DisplayName = SplitPascalCase(method.Name);
                    existing.Description = desc;
                    // Do NOT force IsActive back to true here: an admin's intentional disable must survive restarts. Only brand-new tools default to active (Add branch above).
                }
            }
        }
        await _db.SaveChangesAsync();
        return newlyAdded;
    }

    private static string SplitPascalCase(string input) =>
        PascalCaseBoundaryRegex().Replace(input, " $1");

    // Chèn khoảng trắng trước mỗi chữ HOA (trừ ký tự đầu) để "WriteFile" → "Write File".
    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex PascalCaseBoundaryRegex();
}
