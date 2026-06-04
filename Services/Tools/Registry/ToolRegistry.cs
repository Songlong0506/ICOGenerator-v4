using System.Reflection;
using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Tools.Registry;

public class ToolRegistry : IToolRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AppDbContext _db;

    public ToolRegistry(IServiceProvider serviceProvider, AppDbContext db)
    {
        _serviceProvider = serviceProvider;
        _db = db;
    }

    public async Task<IReadOnlyList<ToolRuntimeDescriptor>> GetToolsForAgentAsync(Guid agentId)
    {
        var defs = await _db.AgentTools
            .Where(x => x.AgentId == agentId && x.ToolDefinition.IsActive)
            .Select(x => x.ToolDefinition)
            .ToListAsync();

        var result = new List<ToolRuntimeDescriptor>();
        foreach (var def in defs)
        {
            var type = ToolDiscoveryService.ToolTypes.FirstOrDefault(x => x.Name == def.ServiceType);
            if (type == null) continue;
            var instance = _serviceProvider.GetRequiredService(type);
            var method = type.GetMethod(def.MethodName, BindingFlags.Instance | BindingFlags.Public);
            if (method == null) continue;
            result.Add(new ToolRuntimeDescriptor(def, instance, method));
        }
        return result;
    }
}
