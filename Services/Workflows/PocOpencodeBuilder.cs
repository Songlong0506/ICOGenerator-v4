using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICOGenerator.Configuration;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Tools;

namespace ICOGenerator.Services.Workflows;

/// <summary>Đầu vào của một lượt <see cref="PocOpencodeBuilder.BuildAsync"/> — cùng các thứ <see cref="PocOneShotRequest"/> mang.</summary>
public sealed record PocOpencodeRequest(
    Guid ProjectId,
    Agent Agent,
    AiModel Model,
    string TaskPrompt,
    string Purpose,
    Action<string, string, string?>? OnProgress);

/// <summary>
/// Dựng bản POC bằng agent CLI <b>opencode</b> thay cho đường dựng của app — chỉ dùng cho lượt "Thử model
/// khác" chọn vỏ opencode (<see cref="Domain.Enums.PocVariantHarness.Opencode"/>), để đo xem VỎ AGENT có
/// làm bản demo tốt hơn không khi model, đầu vào và thước chấm giữ nguyên.
///
/// <para>
/// <b>Giữ cái gì giống bản chính.</b> Prompt bước POC nguyên văn (nạp làm <c>instructions</c> của opencode),
/// cùng template shell, cùng đường ghi vào <c>poc-demo.html</c> (<c>SetPocContent</c>/<c>SetPocScript</c> —
/// cùng marker, cùng parser menu/vai), cùng <c>AuditPocContent</c> và cùng số lượt sửa
/// (<see cref="PocOneShotBuilder.DefaultMaxRepairRounds"/>) do CODE gọi sau mỗi lượt. Khác duy nhất: thay vì
/// một lời gọi model trả về các khối <c>===POC_…===</c>, opencode tự chạy vòng agent của nó (đọc/sửa file,
/// chạy <c>node</c> để kiểm cú pháp…) và giao bốn file trong <c>04_Implementation/poc-src/</c>.
/// </para>
///
/// <para>
/// <b>Vì sao opencode không sửa thẳng poc-demo.html.</b> Shell (sidebar, khối VIEW AS, các marker) do
/// <see cref="PocTemplate"/> dựng và mọi thứ phía sau — POC Review chỉ chỗ theo neo, ghim ghi chú, so sánh
/// biến thể — dựa vào đúng cấu trúc đó. Để một agent ngoài sửa tự do là đem chính các chốt chặn đó ra cược.
/// Nên file demo luôn được dựng lại từ template sạch + bốn file nguồn ở mỗi lượt.
/// </para>
///
/// <para>
/// <b>Chi phí và khoá API.</b> opencode không cầm khoá model nào: nó gọi relay của app
/// (<c>/opencode-relay/v1</c>) bằng token tạm sống đúng bằng lượt này (<see cref="LlmRelaySessions"/>), nên
/// mọi lời gọi đi qua budget guard + call log với cùng <c>Purpose</c> — và chi phí của lượt thử được chốt
/// y như lượt dựng bằng vỏ của app.
/// </para>
///
/// <para>
/// Không có đường rơi về: opencode hỏng (không cài, hết giờ, không giao file) ⇒ lượt thử Failed kèm lý do.
/// Rơi về đường của app sẽ cho ra một cột mang nhãn opencode mà thật ra không phải opencode dựng.
/// </para>
/// </summary>
public class PocOpencodeBuilder
{
    /// <summary>Thư mục bốn file nguồn opencode giao, tương đối với gốc workspace của lượt thử.</summary>
    public const string SourceFolder = "04_Implementation/poc-src";

    public const string MetaFile = "meta.json";
    public const string ContentFile = "content.html";
    public const string ScriptFile = "script.js";
    public const string HandoffFile = "handoff.md";

    /// <summary>Prompt bước POC nguyên văn — opencode nạp nó làm instructions.</summary>
    public const string TaskFile = "poc-task.md";

    /// <summary>Việc của lần chạy hiện tại (dựng / sửa theo báo cáo). Viết vào file để dòng lệnh chỉ mang một câu ASCII cố định.</summary>
    public const string RunFile = "poc-run.md";

    public const string ConfigFile = "opencode.json";

    /// <summary>Biến môi trường mang token relay — token không bao giờ được ghi xuống đĩa.</summary>
    public const string TokenVariable = "ICOGEN_RELAY_TOKEN";

    private const string ProviderId = "icogen";
    private const string ModelKey = "poc";
    private const string BuildPromptKey = "Developer/poc-opencode.v1.md";
    private const string RepairPromptKey = "Developer/poc-opencode-repair.v1.md";

    // Lời nhắn trên dòng lệnh: cố định và CHỈ ASCII. Trên Windows opencode cài qua npm là một file .cmd, và
    // đối số đi qua cmd.exe — đưa prompt/báo cáo audit (có chữ do model viết) lên dòng lệnh là mời lỗi
    // trích dẫn hoặc chèn lệnh. Mọi nội dung thật nằm trong RunFile.
    private const string RunMessage = "Read the file poc-run.md in the current directory and carry out every instruction in it.";

    /// <summary>Số lần chạy DỰNG tối đa khi opencode chạy xong mà không giao đủ file — như <see cref="PocOneShotBuilder.MaxBuildAttempts"/>.</summary>
    public const int MaxBuildAttempts = PocOneShotBuilder.MaxBuildAttempts;

    private const int MaxIssuesInRunFile = 60;

    private readonly WorkspaceTools _tools;
    private readonly PromptTemplateService _prompts;
    private readonly LlmRelaySessions _relaySessions;
    private readonly IPocOpencodeCli _cli;
    private readonly PocOpencodeRelayAddress _relayAddress;
    private readonly PocOpencodeSettings _settings;
    private readonly ILogger<PocOpencodeBuilder> _logger;

    public PocOpencodeBuilder(WorkspaceTools tools, PromptTemplateService prompts, LlmRelaySessions relaySessions,
        IPocOpencodeCli cli, PocOpencodeRelayAddress relayAddress, PocOpencodeSettings settings, ILogger<PocOpencodeBuilder> logger)
    {
        _tools = tools;
        _prompts = prompts;
        _relaySessions = relaySessions;
        _cli = cli;
        _relayAddress = relayAddress;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Dựng bản demo vào <c>04_Implementation/poc-demo.html</c> của workspace mà <see cref="WorkspaceTools"/>
    /// đang trỏ tới (caller đã seed template và nạp bối cảnh audit — y như trước <see cref="PocOneShotBuilder"/>).
    /// Trả bản bàn giao; ném <see cref="InvalidOperationException"/> khi không dựng được.
    /// </summary>
    public async Task<string> BuildAsync(PocOpencodeRequest request, CancellationToken cancellationToken)
    {
        var sandbox = _tools.CurrentWorkspacePath;
        if (string.IsNullOrWhiteSpace(sandbox))
            throw new InvalidOperationException("Workspace chưa được đặt cho lượt dựng POC.");
        var relayBase = _relayAddress.Resolve()
            ?? throw new InvalidOperationException(
                "Không xác định được địa chỉ relay cho opencode — đặt PocOpencode:RelayBaseUrl (vd http://127.0.0.1:5000).");

        _tools.SetRunCancellation(cancellationToken);
        _tools.SetProgressSink(request.OnProgress);
        var mockupPath = _tools.GetSafeFullPath(PocTemplate.MockupRelativePath);
        if (!File.Exists(mockupPath))
            throw new InvalidOperationException($"Không có {PocTemplate.MockupRelativePath} để ghi vào.");
        // Template sạch vừa seed: mỗi lượt ghép lại từ đây, nên một lần opencode lỡ tay sửa poc-demo.html
        // (marker, shell) không bao giờ lọt vào bản được chấm.
        var template = await File.ReadAllTextAsync(mockupPath, cancellationToken);

        Directory.CreateDirectory(Path.Combine(sandbox, SourceFolder));
        await File.WriteAllTextAsync(Path.Combine(sandbox, TaskFile), request.TaskPrompt, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sandbox, ConfigFile), BuildConfig(relayBase, request.Model), cancellationToken);

        var token = _relaySessions.Open(new LlmRelaySession(request.ProjectId, request.Agent.Id, request.Model.Id, request.Purpose));
        try
        {
            return await BuildWithTokenAsync(request, sandbox, mockupPath, template, token, cancellationToken);
        }
        finally
        {
            _relaySessions.Close(token);
        }
    }

    private async Task<string> BuildWithTokenAsync(PocOpencodeRequest request, string sandbox, string mockupPath,
        string template, string token, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var runs = 0;

        // ---- Lượt DỰNG ----
        PocBuildDraft? draft = null;
        string? rejected = null;
        for (var attempt = 1; attempt <= MaxBuildAttempts; attempt++)
        {
            request.OnProgress?.Invoke("thinking", attempt == 1
                ? $"opencode đang dựng POC bằng model {request.Model.ModelId}…"
                : $"opencode chưa giao đủ file ({rejected}) — chạy lại để giao cho đủ…", null);
            await File.WriteAllTextAsync(Path.Combine(sandbox, RunFile), BuildRunMessage(rejected), cancellationToken);
            await RunOrThrowAsync(request, sandbox, token, ++runs, cancellationToken);

            draft = TryReadDraft(sandbox, out var readError);
            if (draft != null)
                break;
            rejected = readError;
            if (attempt == MaxBuildAttempts)
                throw new InvalidOperationException($"opencode chạy xong nhưng không giao được bản demo: {readError} (đã chạy {attempt} lần).");
            notes.Add($"Lần dựng {attempt} chưa giao đủ file ({readError}) — đã chạy lại.");
        }

        var carryOver = await ApplyAsync(mockupPath, template, draft!, cancellationToken);
        var handoff = draft!.Handoff;

        // ---- Tự kiểm → sửa (cùng số lượt với đường của app) ----
        var rounds = 0;
        string? bestHtml = null;
        var bestIssues = int.MaxValue;
        PocVerificationSummary? verdict;
        while (true)
        {
            request.OnProgress?.Invoke("tool", rounds == 0
                ? "Tự kiểm bản POC opencode vừa dựng (audit tĩnh + trình duyệt headless + kịch bản UAT)…"
                : $"Tự kiểm lại sau lượt sửa {rounds}…", null);
            var (report, summary) = await PocOneShotBuilder.AuditAsync(_tools, _logger, cancellationToken);
            verdict = summary;
            if (verdict == null)
            {
                notes.Add("Tự kiểm không chạy được hoặc không ghi được kết quả (poc-verification.json) — dừng vòng sửa.");
                break;
            }

            var remaining = PocOneShotBuilder.IssueCount(verdict);
            if (remaining < bestIssues)
            {
                bestIssues = remaining;
                bestHtml = await File.ReadAllTextAsync(mockupPath, cancellationToken);
            }

            if (remaining == 0 || rounds >= PocOneShotBuilder.DefaultMaxRepairRounds)
                break;

            rounds++;
            request.OnProgress?.Invoke("thinking", $"Còn {remaining} mục cần sửa — opencode sửa lượt {rounds}/{PocOneShotBuilder.DefaultMaxRepairRounds}…", null);
            var before = SourceFingerprint(sandbox);
            await File.WriteAllTextAsync(Path.Combine(sandbox, RunFile), BuildRepairMessage(report, carryOver), cancellationToken);
            var result = await RunAsync(request, sandbox, token, ++runs, cancellationToken);
            if (!result.Succeeded)
            {
                notes.Add($"Lượt sửa {rounds} không chạy xong: {Describe(result)}.");
                break;
            }

            if (SourceFingerprint(sandbox) == before)
            {
                notes.Add($"Lượt sửa {rounds}: opencode không đổi file nguồn nào.");
                break;
            }

            var repaired = TryReadDraft(sandbox, out var readError);
            if (repaired == null)
            {
                notes.Add($"Lượt sửa {rounds} làm hỏng file nguồn ({readError}) — giữ bản trước đó.");
                break;
            }

            handoff = repaired.Handoff.Length > 0 ? repaired.Handoff : handoff;
            carryOver = await ApplyAsync(mockupPath, template, repaired, cancellationToken);
        }

        // Lượt sửa cuối làm TĂNG số mục còn lại ⇒ trả lại bản tốt nhất và kiểm lại, để poc-verification.json
        // khớp với bản demo thật đang nằm trên đĩa (như PocOneShotBuilder).
        var lastIssues = verdict == null ? int.MaxValue : PocOneShotBuilder.IssueCount(verdict);
        if (bestHtml != null && lastIssues > bestIssues)
        {
            await File.WriteAllTextAsync(mockupPath, bestHtml, cancellationToken);
            notes.Add($"Lượt sửa cuối làm tăng số mục còn lại — đã giữ lại bản tốt nhất ({bestIssues} mục).");
            request.OnProgress?.Invoke("tool", "Khôi phục bản tốt nhất và tự kiểm lại…", null);
            (_, verdict) = await PocOneShotBuilder.AuditAsync(_tools, _logger, cancellationToken);
        }

        var method = $"Cách dựng: opencode — {runs} lần chạy (1 lượt dựng"
            + (rounds > 0 ? $" + {rounds} lượt sửa theo tự kiểm" : string.Empty) + ").";
        return PocOneShotBuilder.BuildHandoff(handoff, method, verdict, notes);
    }

    // ---- chạy opencode ----

    private async Task RunOrThrowAsync(PocOpencodeRequest request, string sandbox, string token, int run, CancellationToken cancellationToken)
    {
        var result = await RunAsync(request, sandbox, token, run, cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException($"opencode không chạy xong lượt dựng: {Describe(result)}");
    }

    private Task<PocOpencodeRunResult> RunAsync(PocOpencodeRequest request, string sandbox, string token, int run, CancellationToken cancellationToken)
    {
        // Thư mục cấu hình/dữ liệu riêng của opencode, dùng chung cho mọi lượt thử (giữ cache gói provider
        // giữa các lượt) nhưng tách khỏi cấu hình opencode của tài khoản chạy app — một provider hay khoá API
        // người vận hành tự khai cho opencode của họ không được lọt vào thí nghiệm.
        var home = Path.GetFullPath(Path.Combine(sandbox, "..", "..", ".opencode-home"));
        var environment = new Dictionary<string, string>
        {
            [TokenVariable] = token,
            ["OPENCODE_CONFIG"] = Path.Combine(sandbox, ConfigFile),
            ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(home, "data"),
            ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
            ["XDG_CACHE_HOME"] = Path.Combine(home, "cache"),
            // Relay nằm trên chính máy này: không bao giờ đi vòng qua proxy công ty.
            ["NO_PROXY"] = AppendNoProxy(Environment.GetEnvironmentVariable("NO_PROXY")),
            ["no_proxy"] = AppendNoProxy(Environment.GetEnvironmentVariable("no_proxy"))
        };

        return _cli.RunAsync(new PocOpencodeRun(
            sandbox,
            ["run", "--model", $"{ProviderId}/{ModelKey}", "--title", $"POC run {run}", RunMessage],
            environment,
            Path.Combine(sandbox, $"opencode-run-{run}.log"),
            TimeSpan.FromMinutes(Math.Max(1, _settings.RunTimeoutMinutes)),
            line => request.OnProgress?.Invoke("tool", "opencode: " + (line.Length > 200 ? line[..200] + "…" : line), null),
            [token]),
            cancellationToken);
    }

    private static string AppendNoProxy(string? existing)
    {
        const string local = "127.0.0.1,localhost,::1";
        return string.IsNullOrWhiteSpace(existing) ? local : existing.TrimEnd(',') + "," + local;
    }

    private static string Describe(PocOpencodeRunResult result)
    {
        var head = result.TimedOut ? "quá trần thời gian, đã dừng" : $"thoát với mã {result.ExitCode}";
        var tail = result.OutputTail.Trim();
        if (tail.Length > 600)
            tail = "…" + tail[^600..];
        return tail.Length == 0 ? head : $"{head} — {tail}";
    }

    /// <summary>
    /// <c>opencode.json</c> của lượt thử: một provider OpenAI-compatible trỏ về relay của app (token đọc từ
    /// biến môi trường), quyền tool hẹp — sửa file trong thư mục thử, <c>bash</c> chỉ cho <c>node</c> (kiểm cú
    /// pháp/chạy thử script), không web, không ra ngoài thư mục.
    /// </summary>
    internal static string BuildConfig(string relayBase, AiModel model)
    {
        var modelRef = $"{ProviderId}/{ModelKey}";
        var config = new JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["autoupdate"] = false,
            ["share"] = "disabled",
            ["model"] = modelRef,
            ["small_model"] = modelRef,
            ["instructions"] = new JsonArray(TaskFile),
            ["permission"] = new JsonObject
            {
                ["edit"] = "allow",
                ["webfetch"] = "deny",
                ["external_directory"] = "deny",
                // Luật khớp SAU thắng: cấm mọi lệnh, rồi mở riêng node.
                ["bash"] = new JsonObject { ["*"] = "deny", ["node *"] = "allow" }
            },
            ["provider"] = new JsonObject
            {
                [ProviderId] = new JsonObject
                {
                    ["npm"] = "@ai-sdk/openai-compatible",
                    ["name"] = "ICOGenerator relay",
                    ["options"] = new JsonObject
                    {
                        ["baseURL"] = relayBase.TrimEnd('/') + "/opencode-relay/v1",
                        ["apiKey"] = "{env:" + TokenVariable + "}",
                        // Deadline thật của từng lời gọi là Llm:RequestTimeoutSeconds ở phía relay; trần mặc định
                        // 5 phút của opencode sẽ cắt ngang một lượt model reasoning viết dài.
                        ["timeout"] = false
                    },
                    ["models"] = new JsonObject
                    {
                        [ModelKey] = new JsonObject
                        {
                            ["name"] = model.ModelId,
                            ["limit"] = new JsonObject
                            {
                                ["context"] = model.ContextWindow > 0 ? model.ContextWindow : 128_000,
                                ["output"] = MaxOutputTokenResolver.Resolve(model, 0)
                            }
                        }
                    }
                }
            }
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ---- file nguồn ⇄ bản demo ----

    /// <summary>
    /// Đọc bốn file nguồn thành <see cref="PocBuildDraft"/> qua CHÍNH parser của đường một lượt (ghép lại
    /// thành các khối <c>===POC_…===</c>), nên luật "META phải là JSON đọc được, CONTENT không rỗng" chỉ có
    /// một nguồn.
    /// </summary>
    internal static PocBuildDraft? TryReadDraft(string sandbox, out string error)
    {
        var folder = Path.Combine(sandbox, SourceFolder);
        string Read(string name)
        {
            var path = Path.Combine(folder, name);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }

        var meta = Read(MetaFile);
        if (meta.Trim().Length == 0)
        {
            error = $"thiếu {SourceFolder}/{MetaFile}";
            return null;
        }

        var content = Read(ContentFile);
        if (content.Trim().Length == 0)
        {
            error = $"thiếu hoặc rỗng {SourceFolder}/{ContentFile}";
            return null;
        }

        var script = Read(ScriptFile);
        if (script.Trim().Length == 0)
        {
            error = $"thiếu hoặc rỗng {SourceFolder}/{ScriptFile}";
            return null;
        }

        var reply = new StringBuilder()
            .AppendLine(PocOneShotReply.Marker(PocOneShotReply.Meta)).AppendLine(meta.Trim())
            .AppendLine(PocOneShotReply.Marker(PocOneShotReply.Content)).AppendLine(content.Trim())
            .AppendLine(PocOneShotReply.Marker(PocOneShotReply.Script)).AppendLine(script.Trim())
            .AppendLine(PocOneShotReply.Marker(PocOneShotReply.Handoff)).AppendLine(Read(HandoffFile).Trim())
            .AppendLine(PocOneShotReply.Marker(PocOneShotReply.End))
            .ToString();
        var draft = PocOneShotReply.TryParseBuild(reply, out var parseError);
        error = draft == null ? $"{SourceFolder}: {parseError}" : string.Empty;
        return draft;
    }

    // Template sạch → SetPocContent (vùng nội dung + tên ứng dụng + menu + vai) → SetPocScript: đúng thứ tự
    // và đúng các tool của đường một lượt. Trả các cảnh báo menu/vai để lượt sửa kế biết.
    private async Task<IReadOnlyList<string>> ApplyAsync(string mockupPath, string template, PocBuildDraft draft, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(mockupPath, template, cancellationToken);
        var meta = draft.Meta;
        var reply = await _tools.SetPocContent(draft.Content, meta.AppName, meta.Breadcrumb, meta.NavItems, meta.Roles);
        if (!reply.StartsWith("POC content updated", StringComparison.Ordinal))
            throw new InvalidOperationException($"Không ghép được bản demo từ file nguồn của opencode: {reply}");
        await _tools.SetPocScript(draft.Script);
        return PocOneShotBuilder.ShellWarnings(reply);
    }

    private static string SourceFingerprint(string sandbox)
    {
        var folder = Path.Combine(sandbox, SourceFolder);
        var sb = new StringBuilder();
        foreach (var name in new[] { MetaFile, ContentFile, ScriptFile })
        {
            var path = Path.Combine(folder, name);
            sb.Append(name).Append('\0').Append(File.Exists(path) ? File.ReadAllText(path) : string.Empty).Append('\0');
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private string BuildRunMessage(string? previousRejection)
    {
        var prompt = _prompts.Get(BuildPromptKey).TrimEnd();
        if (previousRejection == null)
            return prompt;
        return prompt + "\n\n## LẦN CHẠY TRƯỚC CHƯA GIAO ĐỦ\n\n"
            + $"Lần chạy trước kết thúc nhưng hệ thống không đọc được bản demo: {previousRejection}. "
            + $"Các file đang có trong `{SourceFolder}/` vẫn còn đó — đọc lại, hoàn thiện cho đủ bốn file rồi mới kết thúc.";
    }

    private string BuildRepairMessage(string report, IReadOnlyList<string> carryOver)
    {
        var sb = new StringBuilder(_prompts.Get(RepairPromptKey).TrimEnd());
        sb.AppendLine().AppendLine();
        sb.AppendLine("# ĐẦU VÀO: Báo cáo AuditPocContent");
        var lines = report.Trim().Split('\n');
        sb.AppendLine(string.Join('\n', lines.Take(MaxIssuesInRunFile * 4)).TrimEnd());
        if (lines.Length > MaxIssuesInRunFile * 4)
            sb.AppendLine("… (báo cáo còn dài hơn — sửa các mục trên trước)");
        if (carryOver.Count > 0)
        {
            sb.AppendLine().AppendLine("# ĐẦU VÀO: Cảnh báo khi ghép meta.json vào shell (menu/vai không đọc được)");
            foreach (var line in carryOver)
                sb.AppendLine("- " + line);
        }
        return sb.ToString();
    }
}
