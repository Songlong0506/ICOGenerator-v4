using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Sinh bộ kịch bản UAT từ AI Design Spec ngay sau khi POC dựng xong, lưu tại
/// <c>04_Implementation/uat-scenarios.json</c> cạnh poc-demo.html. Trang POC Review đọc file này để
/// render checklist "đi từng bước" — biến review POC từ khám phá tự do (user thường không biết nên thử
/// gì) thành UAT có kịch bản; bước fail thì ghim luôn ghi chú cho Developer. FAIL-OPEN toàn phần: sinh
/// lỗi/không có BA ⇒ bỏ qua, trang review chỉ đơn giản không có checklist (như trước đây).
/// </summary>
public class UatScenarioService
{
    public const string FileName = "uat-scenarios.json";
    private const int MaxScenarios = 8;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly BAAgentResolver _agentResolver;
    private readonly ILogger<UatScenarioService> _logger;

    public UatScenarioService(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        WorkspacePathResolver workspacePathResolver,
        BAAgentResolver agentResolver,
        ILogger<UatScenarioService> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _workspacePathResolver = workspacePathResolver;
        _agentResolver = agentResolver;
        _logger = logger;
    }

    /// <summary>
    /// Sinh và ghi file kịch bản cho project (ghi đè bản cũ — spec là nguồn duy nhất nên chạy lại là
    /// làm tươi). Mọi lỗi đều được nuốt + log: đây là bước phụ trợ, không được làm fail task POC.
    /// </summary>
    public async Task TryGenerateAsync(Guid projectId, string aiDesignSpec, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(aiDesignSpec))
                return;

            var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
            if (ba == null)
                return;
            var model = ba.AiModel!;

            var project = await _db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project == null)
                return;

            var prompt = _prompts.Get("BusinessAnalyst/uat-scenarios.v1.md")
                .Replace("{{input}}", aiDesignSpec);

            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

            var (callResult, structured) = await _llm.ChatStructuredAsync<UatScenarioSet>(
                model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAUatScenarios", workflowRunId),
                cancellationToken: cancellationToken);

            if (!callResult.IsSuccess)
            {
                _logger.LogWarning("UAT scenario generation failed for project {ProjectId}: {Error}", projectId, callResult.ErrorMessage ?? callResult.Content);
                return;
            }

            var set = structured ?? ParseFallback(callResult.Content);
            set = Sanitize(set);
            if (set.Scenarios.Count == 0)
                return;

            var path = GetScenarioPath(project.Id, project.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(set, WriteOptions), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown thật sự thì để caller xử lý như mọi bước khác.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not generate UAT scenarios for project {ProjectId}.", projectId);
        }
    }

    /// <summary>Đọc bộ kịch bản đã lưu của project; không có/hỏng ⇒ bộ rỗng (trang review tự ẩn panel).</summary>
    public async Task<UatScenarioSet> LoadAsync(Guid projectId, string projectName, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = GetScenarioPath(projectId, projectName);
            if (!File.Exists(path))
                return new UatScenarioSet();

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return Sanitize(JsonSerializer.Deserialize<UatScenarioSet>(json, ReadOptions) ?? new UatScenarioSet());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read UAT scenarios for project {ProjectId}.", projectId);
            return new UatScenarioSet();
        }
    }

    private string GetScenarioPath(Guid projectId, string projectName)
    {
        var mockupPath = _workspacePathResolver.GetMockupPath(WorkspacePathResolver.GetWorkspaceFolder(projectId, projectName));
        return Path.Combine(Path.GetDirectoryName(mockupPath)!, FileName);
    }

    private static UatScenarioSet ParseFallback(string? raw)
    {
        try
        {
            var json = JsonExtractor.Extract(raw ?? string.Empty);
            if (json.Length == 0)
                return new UatScenarioSet();
            return JsonSerializer.Deserialize<UatScenarioSet>(json, ReadOptions) ?? new UatScenarioSet();
        }
        catch
        {
            return new UatScenarioSet();
        }
    }

    // Chặn dữ liệu rác của model: bỏ kịch bản không tên/không bước, giới hạn số lượng.
    private static UatScenarioSet Sanitize(UatScenarioSet set)
    {
        set.Scenarios = set.Scenarios
            .Where(s => !string.IsNullOrWhiteSpace(s.Title) && s.Steps.Any(x => !string.IsNullOrWhiteSpace(x)))
            .Take(MaxScenarios)
            .ToList();
        foreach (var s in set.Scenarios)
        {
            s.Title = s.Title.Trim();
            s.Screen = (s.Screen ?? string.Empty).Trim();
            s.Steps = s.Steps.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            s.RuleRefs = (s.RuleRefs ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        }
        return set;
    }
}
