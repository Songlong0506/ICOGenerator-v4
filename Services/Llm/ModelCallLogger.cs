using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Llm;

public class ModelCallLogger : IModelCallLogger
{
    private readonly AppDbContext _db;
    private readonly ModelCallImageStore _imageStore;

    public ModelCallLogger(AppDbContext db, ModelCallImageStore imageStore)
    {
        _db = db;
        _imageStore = imageStore;
    }

    public async Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null)
    {
        var log = new AgentModelCallLog
        {
            ProjectId = projectId,
            AgentId = agent.Id,
            WorkflowRunId = workflowRunId,
            AgentName = agent.RoleKey.GetTitle(),
            ModelId = callResult.ModelId,
            RequestJson = callResult.RequestJson,
            ResponseText = callResult.ResponseText,
            ErrorMessage = callResult.ErrorMessage,
            PromptTokens = callResult.PromptTokens,
            CachedPromptTokens = callResult.CachedPromptTokens,
            CompletionTokens = callResult.CompletionTokens,
            TotalTokens = callResult.TotalTokens,
            DurationMs = callResult.DurationMs,
            HttpStatusCode = callResult.HttpStatusCode,
            IsSuccess = callResult.IsSuccess,
            Step = step,
            Purpose = purpose
        };

        _db.AgentModelCallLogs.Add(log);
        await _db.SaveChangesAsync();
        await SaveRequestImagesAsync(projectId, log.Id, callResult.RequestImages);
    }

    // Ảnh đi kèm lời gọi được ghi ra ĐĨA sau khi dòng log đã lưu thành công (insert hỏng thì không để lại
    // file mồ côi). Chỉ chạm DB thêm một lần cho các lời gọi THẬT SỰ có ảnh — tuyệt đại đa số lượt không có,
    // nên đường nóng không đổi. Tên project cần để dựng đường workspace: đổi tên project là đổi tên thư
    // mục, nên phải lấy tên HIỆN TẠI chứ không cache lại.
    private async Task SaveRequestImagesAsync(Guid projectId, Guid logId, IReadOnlyList<ModelCallImage> images)
    {
        if (images.Count == 0)
            return;

        var projectName = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync();
        if (projectName == null)
            return;

        _imageStore.Save(projectId, projectName, logId, images);
    }
}
