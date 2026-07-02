using System.ComponentModel;
using System.Text.Json;
using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Tools;

public class WorkspaceTools
{
    private readonly IConfiguration _configuration;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public WorkspaceTools(IConfiguration configuration, WorkspacePathResolver workspacePathResolver)
    {
        _configuration = configuration;
        _workspacePathResolver = workspacePathResolver;
    }
    public string CurrentWorkspacePath { get; private set; } = string.Empty;

    // Ambient cancellation for the current agent run, shared with long-running tools (e.g. CommandTools)
    // so a workflow cancel / app shutdown actually stops a spawned process instead of waiting out the timeout.
    public CancellationToken RunCancellationToken { get; private set; } = CancellationToken.None;

    public void SetWorkspace(string projectKey)
    {
        CurrentWorkspacePath = _workspacePathResolver.GetProjectWorkspacePath(projectKey);
        Directory.CreateDirectory(CurrentWorkspacePath);
    }

    public void SetRunCancellation(CancellationToken cancellationToken) => RunCancellationToken = cancellationToken;

    [Description("Write a source code or documentation file into the current workspace.")]
    public async Task<string> WriteFile(string relativePath, string content)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(relativePath);
        ValidateExtension(fullPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fullPath, content);
        return $"File written: {relativePath}";
    }

    [Description("Write MULTIPLE files into the current workspace in ONE call. " +
        "'files' (required): an array of objects { \"path\": string (relative path), \"content\": string (the full file contents) }. " +
        "STRONGLY PREFER this over many separate WriteFile calls when generating a multi-file project: each agent step is one tool call, so batching 10–20 files per call keeps a large project (dozens/hundreds of files) within the step budget. " +
        "Each file is written independently; a failure on one (e.g. disallowed extension) is reported but does not block the rest. Returns a per-file summary of what was written and what failed.")]
    public async Task<string> WriteFiles(FileWrite[] files)
    {
        EnsureWorkspace();
        if (files == null || files.Length == 0) return "No files provided.";

        var written = new List<string>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Path))
            {
                errors.Add("(missing path): a file entry has no 'path'.");
                continue;
            }
            try
            {
                var fullPath = GetSafeFullPath(file.Path);
                ValidateExtension(fullPath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(fullPath, file.Content ?? string.Empty);
                written.Add(file.Path);
            }
            catch (Exception ex)
            {
                errors.Add($"{file.Path}: {ex.Message}");
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"Wrote {written.Count} file(s)");
        if (written.Count > 0) sb.Append(": ").Append(string.Join(", ", written));
        sb.Append('.');
        if (errors.Count > 0)
            sb.Append($" Failed {errors.Count}: ").Append(string.Join("; ", errors)).Append('.');
        return sb.ToString();
    }

    private const int MaxFullReadBytes = 200 * 1024;

    [Description("Read a file from the current workspace. Files under 200 KB are returned in full. Only larger files are paginated: pass offset (a line number) to read the next chunk, and the response will end with '[truncated: use offset=N to continue reading]' telling you the exact next line.")]
    public async Task<string> ReadFile(string relativePath, int offset = 0)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(relativePath);
        if (!File.Exists(fullPath)) return $"File not found: {relativePath}";

        var fileSize = new FileInfo(fullPath).Length;
        if (fileSize <= MaxFullReadBytes)
            return await File.ReadAllTextAsync(fullPath);

        var lines = await File.ReadAllLinesAsync(fullPath);
        if (offset < 0) offset = 0;
        if (offset >= lines.Length) return $"Offset {offset} exceeds file length ({lines.Length} lines).";

        const int maxChars = 16000;
        var sb = new System.Text.StringBuilder();
        var i = offset;
        while (i < lines.Length)
        {
            var line = $"{i + 1}: {lines[i]}\n";
            if (sb.Length + line.Length > maxChars) break;
            sb.Append(line);
            i++;
        }

        if (i < lines.Length)
            sb.Append($"\n...[truncated: use offset={i} to continue reading]");

        return sb.ToString();
    }

    [Description("List all files in the current workspace.")]
    public string ListFiles()
    {
        EnsureWorkspace();
        var files = Directory.EnumerateFiles(CurrentWorkspacePath, "*.*", SearchOption.AllDirectories)
            .Where(x => !WorkspaceFileFilter.IsInRegenerableDirectory(CurrentWorkspacePath, x))
            .Select(x => Path.GetRelativePath(CurrentWorkspacePath, x)).Take(500).ToList();
        return files.Count == 0 ? "No files found." : string.Join(Environment.NewLine, files);
    }

    [Description("Search files by keyword in relative path.")]
    public string SearchFiles(string keyword)
    {
        EnsureWorkspace();
        var files = Directory.EnumerateFiles(CurrentWorkspacePath, "*.*", SearchOption.AllDirectories)
            .Where(x => !WorkspaceFileFilter.IsInRegenerableDirectory(CurrentWorkspacePath, x))
            .Select(x => Path.GetRelativePath(CurrentWorkspacePath, x))
            .Where(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase)).Take(100).ToList();
        return files.Count == 0 ? "No matched files." : string.Join(Environment.NewLine, files);
    }

    [Description("Replace text in an existing file.")]
    public async Task<string> ReplaceInFile(string relativePath, string oldText, string newText)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(relativePath);
        if (!File.Exists(fullPath)) return $"File not found: {relativePath}";
        var content = await File.ReadAllTextAsync(fullPath);
        if (!content.Contains(oldText)) return $"Old text not found in file: {relativePath}";
        await File.WriteAllTextAsync(fullPath, content.Replace(oldText, newText));
        return $"File updated: {relativePath}";
    }

    [Description("Set the POC feature UI AND customise the page shell for the generated demo, in ONE call. " +
        "'content' (required): only the inner HTML for the content region (no <html>/<head>/<body>/sidebar/topbar); it is placed between the POC_CONTENT markers in 04_Implementation/poc-demo.html. " +
        "For WORKING, PERSISTENT CRUD with NO custom <script>, annotate the content with data-crud-* attributes and the shell engine handles add/edit/delete + localStorage automatically: a list <table data-crud-table=\"ENTITY\" data-crud-modal=\"#id\"> whose <thead> has one <th data-field=\"FIELD\"> per column plus a final <th data-actions> (rows you write in <tbody> are seed data); exactly ONE <form data-crud-form=\"ENTITY\"> (inputs use name=\"FIELD\", with a type=submit Save), usually inside a Bootstrap modal; an Add button with data-crud-add=\"ENTITY\"; optional data-crud-title/data-crud-count/data-crud-reset. The form's name=\"…\" must match the table's data-field names. Seed <td> cells may contain rich HTML (images, badges) and are preserved. For a one-click add with no form (e.g. an Add-to-cart/Buy button), put data-crud-add=\"ENTITY\" data-crud-values='{\"field\":\"value\"}' on the button. Wire EVERY data list (data-crud-table + an Add button) and EVERY create/edit form (data-crud-form) this way so the primary actions actually work and persist — don't leave the main buttons as toast-only stubs. " +
        "'appName': the application/product name, shown in the sidebar header and the browser tab — never leave it as the template default \"App Name\". " +
        "'breadcrumb': the top-bar breadcrumb text, e.g. \"Home > Orders\". " +
        "'navItems': the left sidebar menu — an array of objects { \"label\": string, \"icon\"?: string, \"children\"?: (string | { \"label\": string, \"icon\"?: string })[] }. 'icon' is an optional Bootstrap Icons name shown before the label (e.g. \"house\", \"cart3\", \"people\", \"bag\", \"gear\"; full list at https://icons.getbootstrap.com — the leading \"bi-\" is optional); set one for every item, and if omitted a neutral default icon is used. 'children' is optional and turns the entry into an expandable group. Set these to the real screens, not the template's Overview/Module A/Module B/Settings. " +
        "The rest of the shell (style/script, topbar, popups) is kept untouched. Use this instead of ReplaceInFile for the POC.")]
    public async Task<string> SetPocContent(string content, string? appName = null, string? breadcrumb = null, JsonElement? navItems = null)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(PocTemplate.MockupRelativePath);
        if (!File.Exists(fullPath)) return $"File not found: {PocTemplate.MockupRelativePath}";

        var current = await File.ReadAllTextAsync(fullPath);
        var updated = PocTemplate.ReplaceContent(current, content ?? string.Empty);
        if (updated == null)
            return $"POC content markers not found in file: {PocTemplate.MockupRelativePath}";

        // Customise the shell bits outside the content markers (App Name, title, breadcrumb, menu). Each step no-ops on empty/malformed input, so a slip in one never blocks the others or the content — worst case the shell stays as the template, never a failure.
        if (!string.IsNullOrWhiteSpace(appName))
            updated = PocTemplate.ReplaceAppName(updated, appName);
        if (!string.IsNullOrWhiteSpace(breadcrumb))
            updated = PocTemplate.ReplaceBreadcrumb(updated, breadcrumb);
        if (navItems is { } navJson)
        {
            var items = PocNavItem.ParseList(navJson);
            if (items.Count > 0)
                updated = PocTemplate.ReplaceNav(updated, items);
        }

        await File.WriteAllTextAsync(fullPath, updated);
        return $"POC content updated: {PocTemplate.MockupRelativePath}";
    }

    [Description("Append MORE feature HTML to the END of the POC content region in 04_Implementation/poc-demo.html, after what's already there. " +
        "Use this to build a large POC across SEVERAL small calls so no single call has to carry the whole page — a single big call is what gets cut off at the token limit (finish_reason=length) and fails. " +
        "Flow: call SetPocContent ONCE first (it sets appName/breadcrumb/navItems and writes the FIRST screen, replacing the region), then call AppendPocContent once per REMAINING `<section class=\"page-view\" data-view=\"…\">` screen and once per modal. Do NOT call SetPocContent again — it would overwrite everything appended so far. " +
        "'content' (required): the inner HTML to append — same rules as SetPocContent's content (no <html>/<head>/<body>/sidebar/topbar; wrap each screen in its own page-view section; place modals after the sections). Keep EACH call small so it fits in one response. " +
        "When every screen and modal has been appended, return your final result — do not re-read the file.")]
    public async Task<string> AppendPocContent(string content)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(PocTemplate.MockupRelativePath);
        if (!File.Exists(fullPath)) return $"File not found: {PocTemplate.MockupRelativePath}";
        if (string.IsNullOrWhiteSpace(content)) return "No content to append.";

        var current = await File.ReadAllTextAsync(fullPath);
        var updated = PocTemplate.AppendContent(current, content);
        if (updated == null)
            return $"POC content markers not found in file: {PocTemplate.MockupRelativePath}";

        await File.WriteAllTextAsync(fullPath, updated);
        return $"POC content appended: {PocTemplate.MockupRelativePath}";
    }

    [Description("Set the POC page-logic JavaScript of 04_Implementation/poc-demo.html (REPLACES the dedicated POC_SCRIPT region; the shell script, Bootstrap and the data-crud-* engine stay untouched and keep working). " +
        "Call it ONCE, AFTER all SetPocContent/AppendPocContent calls, to make the AI Design Spec's business rules actually behave in the demo: compute derived values (totals, weighted averages, ratings) from the seed data instead of hard-coding numbers, drive status/sign state transitions (lock/unlock controls, swap badges, revoke signatures on edit), and simulate roles — after a fake login/persona pick, show only that role's sidebar items and screens. " +
        "'script' (required): PURE JavaScript only — no <script> tag, no external libraries/CDN, no frameworks; plain DOM APIs. It runs AFTER the shell script, so window.pocToast(msg) shows the standard toast and window.pocNavigate(label) opens a screen exactly like a sidebar click (falling back to a direct view switch for screens not in the menu, e.g. Login). " +
        "Declare functions globally so onclick=\"…\" attributes in the content can call them, and put data-no-toast on buttons this script fully handles so the shell's generic click-toast doesn't double up. " +
        "If the logic is too long for one call, send the core here and add the rest with AppendPocScript (split at whole-function boundaries).")]
    public async Task<string> SetPocScript(string script)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(PocTemplate.MockupRelativePath);
        if (!File.Exists(fullPath)) return $"File not found: {PocTemplate.MockupRelativePath}";
        if (string.IsNullOrWhiteSpace(script)) return "No script provided.";

        var current = await File.ReadAllTextAsync(fullPath);
        var updated = PocTemplate.ReplaceScript(current, script);
        if (updated == null)
            return $"POC script region not found (and no </body> to graft it into): {PocTemplate.MockupRelativePath}";

        await File.WriteAllTextAsync(fullPath, updated);
        return $"POC script updated: {PocTemplate.MockupRelativePath}";
    }

    [Description("Append MORE JavaScript to the END of the POC_SCRIPT region of 04_Implementation/poc-demo.html, after what SetPocScript wrote, so long page logic can be delivered across several small calls instead of one big call that gets cut off at the token limit (finish_reason=length). " +
        "Same rules as SetPocScript's 'script': pure JavaScript only — no <script> tag, no external libraries. Chunks share one script element and run in order, so split at whole-function boundaries (never mid-function or mid-string).")]
    public async Task<string> AppendPocScript(string script)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(PocTemplate.MockupRelativePath);
        if (!File.Exists(fullPath)) return $"File not found: {PocTemplate.MockupRelativePath}";
        if (string.IsNullOrWhiteSpace(script)) return "No script provided.";

        var current = await File.ReadAllTextAsync(fullPath);
        var updated = PocTemplate.AppendScript(current, script);
        if (updated == null)
            return $"POC script region not found (and no </body> to graft it into): {PocTemplate.MockupRelativePath}";

        await File.WriteAllTextAsync(fullPath, updated);
        return $"POC script appended: {PocTemplate.MockupRelativePath}";
    }

    [Description("Audit the generated POC (04_Implementation/poc-demo.html) and report concrete defects to fix before finishing: sidebar menu items without a matching page-view section (clicking them would change nothing), sections unreachable from the menu, duplicate element ids or reuse of the shell's reserved ids, modal triggers pointing at missing ids, data-crud tables without a matching form or with mismatched field names, and whether the POC logic script is still empty. " +
        "Call it ONCE after all content and script calls, fix every reported ISSUE (AppendPocContent for missing sections/modals, ReplaceInFile for small in-place corrections, SetPocScript to replace the logic), then return your final result. It reads the file for you — do NOT re-read poc-demo.html with ReadFile.")]
    public async Task<string> AuditPocContent()
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(PocTemplate.MockupRelativePath);
        if (!File.Exists(fullPath)) return $"File not found: {PocTemplate.MockupRelativePath}";

        var current = await File.ReadAllTextAsync(fullPath);
        return PocAudit.Run(current);
    }

    public void RenameFolder(string oldRelativePath, string newRelativePath)
    {
        EnsureWorkspace();

        var oldFullPath = GetSafeFullPath(oldRelativePath);
        var newFullPath = GetSafeFullPath(newRelativePath);

        if (!Directory.Exists(oldFullPath))
            throw new InvalidOperationException($"Folder not found: {oldRelativePath}");

        if (Directory.Exists(newFullPath))
            throw new InvalidOperationException($"Target folder already exists: {newRelativePath}");

        Directory.Move(oldFullPath, newFullPath);
    }

    private string GetSafeFullPath(string relativePath)
    {
        return _workspacePathResolver.GetSafeFullPath(CurrentWorkspacePath, relativePath);
    }
    private void ValidateExtension(string fullPath)
    {
        var allowed = _configuration.GetSection("AllowedFileExtensions").Get<string[]>() ?? [];
        if (allowed.Length == 0) return;
        // Match the true file extension exactly (e.g. ".cs") and reject extensionless names, rather than
        // a loose suffix check on the whole file name.
        var ext = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(ext) || !allowed.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"File extension is not allowed: {Path.GetFileName(fullPath)}");
    }
    private void EnsureWorkspace()
    {
        if (string.IsNullOrWhiteSpace(CurrentWorkspacePath)) throw new InvalidOperationException("Workspace is not initialized.");
    }
}

/// <summary>One file to write in a <see cref="WorkspaceTools.WriteFiles"/> batch: a relative path and its full contents.</summary>
public record FileWrite(string Path, string Content);
