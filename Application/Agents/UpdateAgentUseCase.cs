using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public class UpdateAgentUseCase
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;

    public UpdateAgentUseCase(AppDbContext db, IAuditLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<UpdateAgentResult> ExecuteAsync(AgentEditVm vm)
    {
        var agent = await _db.Agents.Include(x => x.AgentTools).FirstOrDefaultAsync(x => x.Id == vm.Id);
        if (agent == null)
            return UpdateAgentResult.NotFound;

        // Mỗi agent bắt buộc phải gán một AI model còn tồn tại — set thủ công để
        // tránh chạy nhầm model ngoài ý muốn.
        if (vm.AiModelId is not { } modelId || !await _db.AiModels.AnyAsync(x => x.Id == modelId))
            return UpdateAgentResult.ModelRequired;

        // Chụp trạng thái TRƯỚC khi sửa để so sánh trong audit log.
        var before = Snapshot(agent);

        agent.Name = vm.Name?.Trim() ?? string.Empty;
        agent.Description = vm.Description?.Trim() ?? string.Empty;
        agent.Color = string.IsNullOrWhiteSpace(vm.Color) ? "#8B5CF6" : vm.Color.Trim();
        agent.Temperature = vm.Temperature;
        agent.AiModelId = modelId;

        var selectedToolIds = vm.ToolDefinitionIds.Distinct().ToHashSet();
        var removed = agent.AgentTools.Where(x => !selectedToolIds.Contains(x.ToolDefinitionId)).ToList();
        _db.AgentTools.RemoveRange(removed);

        var existingToolIds = agent.AgentTools.Select(x => x.ToolDefinitionId).ToHashSet();
        var newToolIds = selectedToolIds.Where(id => !existingToolIds.Contains(id)).ToList();
        // Lọc các tool id hợp lệ (còn tồn tại và đang active) bằng MỘT truy vấn thay vì AnyAsync từng id.
        var validNewToolIds = newToolIds.Count == 0
            ? []
            : await _db.ToolDefinitions
                .Where(x => newToolIds.Contains(x.Id) && x.IsActive)
                .Select(x => x.Id)
                .ToListAsync();
        foreach (var toolId in validNewToolIds)
            _db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolDefinitionId = toolId });

        await _db.SaveChangesAsync();

        await _audit.LogAsync(AuditCategory.Agent, AuditAction.Update, agent.Id.ToString(),
            $"Cập nhật Agent \"{agent.Name}\"", before: before, after: Snapshot(agent));
        return UpdateAgentResult.Success;
    }

    // Ảnh chụp cấu hình agent (kèm danh sách tool đã gán) để so sánh before/after trong audit log.
    private static object Snapshot(Agent a) => new
    {
        a.Name,
        a.Description,
        a.Color,
        a.Temperature,
        AiModelId = a.AiModelId.ToString(),
        ToolDefinitionIds = a.AgentTools.Select(t => t.ToolDefinitionId).OrderBy(id => id).ToList()
    };
}
