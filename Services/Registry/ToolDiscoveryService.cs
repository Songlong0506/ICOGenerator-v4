using System.ComponentModel;
using System.Reflection;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Registry;

public class ToolDiscoveryService
{
    private readonly AppDbContext _db;
    public ToolDiscoveryService(AppDbContext db) { _db = db; }

    public static Type[] ToolTypes =>
    [
        typeof(WorkspaceTools),
        typeof(CommandTools),
        typeof(GitTools),
        typeof(DiffTools)
    ];

    public async Task SyncToolDefinitionsAsync()
    {
        foreach (var type in ToolTypes)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.GetCustomAttribute<DescriptionAttribute>() != null);

            foreach (var method in methods)
            {
                var exists = await _db.ToolDefinitions.FirstOrDefaultAsync(x => x.ServiceType == type.Name && x.MethodName == method.Name);
                var desc = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
                if (exists == null)
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
                    exists.DisplayName = SplitPascalCase(method.Name);
                    exists.Description = desc;
                    exists.IsActive = true;
                }
            }
        }
        await _db.SaveChangesAsync();
    }

    private static string SplitPascalCase(string input) =>
        System.Text.RegularExpressions.Regex.Replace(input, "(?<!^)([A-Z])", " $1");
}
