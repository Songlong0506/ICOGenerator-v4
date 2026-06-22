using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Workflows;

/// <summary>
/// Seeds the POC design assets into a project's workspace before the POC-preview stage runs: copies the
/// POC template and pre-seeds <c>poc-demo.html</c> with a single editable placeholder region (so the
/// dev agent edits only the content area, not the whole shell — saves tokens and avoids brittle
/// whole-block replacements).
///
/// Extracted from AgentTaskWorker so both the legacy DB-task worker and the opt-in MAF workflow engine
/// share one implementation instead of duplicating the file plumbing.
/// </summary>
public sealed class PocWorkspaceSeeder
{
    private readonly WorkspacePathResolver _resolver;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<PocWorkspaceSeeder> _logger;

    public PocWorkspaceSeeder(WorkspacePathResolver resolver, IWebHostEnvironment environment, ILogger<PocWorkspaceSeeder> logger)
    {
        _resolver = resolver;
        _environment = environment;
        _logger = logger;
    }

    public async Task EnsureDesignAssetsAsync(Project project)
    {
        try
        {
            var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
            var implDir = Path.GetDirectoryName(_resolver.GetMockupPath(projectKey));
            if (string.IsNullOrWhiteSpace(implDir))
                return;

            Directory.CreateDirectory(implDir);

            // Resolve from ContentRootPath so this and PromptTemplateService share the same "Prompts" root
            // (BaseDirectory = bin output diverged from project root).
            var sourceDir = Path.Combine(_environment.ContentRootPath, "Prompts", "Design");
            foreach (var name in new[] { "poc-template.html" })
            {
                var src = Path.Combine(sourceDir, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(implDir, name), overwrite: true);
            }

            // Pre-seed poc-demo.html so the dev agent edits only the content region, not re-emitting the
            // whole shell (saves tokens per run). The marker region is collapsed to a SINGLE placeholder so
            // one deterministic ReplaceInFile works, vs reproducing the ~160-line block verbatim.
            var templateSrc = Path.Combine(sourceDir, "poc-template.html");
            if (File.Exists(templateSrc))
                await SeedPocDemoAsync(templateSrc, _resolver.GetMockupPath(projectKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy POC design assets into the workspace.");
        }
    }

    // Copies the template into poc-demo.html, replacing the POC_CONTENT region with a placeholder but
    // keeping the markers as a stable editable region.
    private static async Task SeedPocDemoAsync(string templateSrc, string demoPath)
    {
        var template = await File.ReadAllTextAsync(templateSrc);
        var seeded = PocTemplate.SeedFromTemplate(template);

        if (seeded == null)
        {
            // Markers missing/malformed: fall back to a raw copy so we never lose the file.
            File.Copy(templateSrc, demoPath, overwrite: true);
            return;
        }

        await File.WriteAllTextAsync(demoPath, seeded);
    }
}
