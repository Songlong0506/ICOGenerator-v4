using System.Text.Json.Nodes;
using ICOGenerator.Services.Settings;

namespace ICOGenerator.Application.Settings;

public class GetAppSettingsQuery
{
    private readonly AppSettingsFileStore _store;
    public GetAppSettingsQuery(AppSettingsFileStore store) => _store = store;

    public async Task<AppSettingsVm> ExecuteAsync()
    {
        var root = await _store.ReadAsync();

        return new AppSettingsVm
        {
            ConnectionString = root["ConnectionStrings"]?["DefaultConnection"]?.GetValue<string>() ?? "",
            WorkspaceRootPath = root["AgentWorkspace"]?["RootPath"]?.GetValue<string>() ?? "",
            AllowedCommands = JoinLines(root["AllowedCommands"]),
            AllowedFileExtensions = JoinLines(root["AllowedFileExtensions"]),
            PullRequestRemoteName = root["PullRequest"]?["RemoteName"]?.GetValue<string>() ?? "origin",
            AllowedHosts = root["AllowedHosts"]?.GetValue<string>() ?? "*"
        };
    }

    private static string JoinLines(JsonNode? node) =>
        node is JsonArray array
            ? string.Join("\n", array
                .Select(x => x?.GetValue<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            : "";
}
