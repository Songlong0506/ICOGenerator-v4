using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Tools.Registry;
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

        agent.Description = vm.Description?.Trim() ?? string.Empty;
        agent.Color = string.IsNullOrWhiteSpace(vm.Color) ? "#8B5CF6" : vm.Color.Trim();
        agent.Temperature = vm.Temperature;
        agent.AiModelId = modelId;

        var selectedToolIds = vm.ToolDefinitionIds.Distinct().ToHashSet();
        await ExpandAllOrNothingGroupsAsync(selectedToolIds);
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
            $"Cập nhật Agent \"{agent.RoleKey.GetTitle()}\"", before: before, after: Snapshot(agent));
        return UpdateAgentResult.Success;
    }

    /// <summary>
    /// Nhóm tool khai <see cref="ToolGroupAllOrNothingAttribute"/> không chia nhỏ được: chọn MỘT tool
    /// trong nhóm là cấp cả nhóm. Không chọn cái nào thì vẫn là không cấp gì — khoá nhóm là bỏ đi một độ
    /// chi tiết không có thật, không phải biến nhóm thành thứ không gỡ được.
    ///
    /// <para>
    /// Chốt ở TẦNG USE CASE chứ không ở JavaScript: màn hình Agents chỉ hiện một ô tick cho cả nhóm, nhưng
    /// một form POST gửi tay hay một tab còn mở bản giao diện cũ vẫn gửi lên được tập con — và một cấu hình
    /// nửa vời thì agent không lái nổi trình duyệt mà cũng chẳng có gì báo lỗi.
    /// </para>
    /// </summary>
    private async Task ExpandAllOrNothingGroupsAsync(HashSet<Guid> selectedToolIds)
    {
        var lockedGroups = ToolDiscoveryService.AllOrNothingGroups.Select(g => g.ServiceType).ToList();
        if (lockedGroups.Count == 0 || selectedToolIds.Count == 0)
            return;

        var lockedTools = await _db.ToolDefinitions
            .Where(x => lockedGroups.Contains(x.ServiceType))
            .Select(x => new { x.Id, x.ServiceType })
            .ToListAsync();

        foreach (var group in lockedTools.GroupBy(x => x.ServiceType))
        {
            if (!group.Any(t => selectedToolIds.Contains(t.Id)))
                continue;

            foreach (var tool in group)
                selectedToolIds.Add(tool.Id);
        }
    }

    // Ảnh chụp cấu hình agent (kèm danh sách tool đã gán) để so sánh before/after trong audit log.
    private static object Snapshot(Agent a) => new
    {
        RoleKey = a.RoleKey.ToString(),
        a.Description,
        a.Color,
        a.Temperature,
        AiModelId = a.AiModelId.ToString(),
        ToolDefinitionIds = a.AgentTools.Select(t => t.ToolDefinitionId).OrderBy(id => id).ToList()
    };
}
