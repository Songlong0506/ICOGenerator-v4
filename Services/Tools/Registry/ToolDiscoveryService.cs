using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Tools;
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
        typeof(GitTools),
        typeof(WebTools)
    ];

    /// <summary>
    /// Các nhóm tool khai <see cref="ToolGroupAllOrNothingAttribute"/> — bật là bật cả gói. Khoá nhóm là
    /// <c>type.Name</c>, đúng giá trị đang ghi vào <c>ToolDefinition.ServiceType</c> bên dưới, nên tầng
    /// Application/View tra thẳng bằng khoá nhóm mà không cần biết gì về reflection.
    /// </summary>
    public static IReadOnlyList<LockedToolGroup> AllOrNothingGroups { get; } = ToolTypes
        .Select(t => (Type: t, Attr: t.GetCustomAttribute<ToolGroupAllOrNothingAttribute>()))
        .Where(x => x.Attr != null)
        .Select(x => new LockedToolGroup(x.Type.Name, x.Attr!.DisplayName, x.Attr.Description))
        .ToList();

    public async Task SyncToolDefinitionsAsync()
    {
        // Load every existing definition once and match in memory, rather than a DB round-trip per tool
        // method (one query each). The set is tiny and the (ServiceType, MethodName) pair is unique.
        var existingByKey = (await _db.ToolDefinitions.ToListAsync())
            .ToDictionary(x => (x.ServiceType, x.MethodName));

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
    }

    private static string SplitPascalCase(string input) =>
        PascalCaseBoundaryRegex().Replace(input, " $1");

    // Chèn khoảng trắng trước mỗi chữ HOA (trừ ký tự đầu) để "WriteFile" → "Write File".
    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex PascalCaseBoundaryRegex();
}
