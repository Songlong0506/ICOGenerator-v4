using System.Diagnostics;
using System.Text;
using TeamExport.Stripping;

namespace TeamExport;

public sealed record ExportOptions(
    string TargetDirectory,
    string? ConfigPath,
    bool Build,
    bool Commit,
    bool Push,
    bool AllowDirty);

/// <summary>
/// Xuất repo gốc sang bản clone của repo team: chọn file, xoá comment, biến đổi appsettings, chép
/// overlay, build thử, rồi (tuỳ chọn) commit + push. Luồng một chiều: repo team không bao giờ là nguồn.
/// </summary>
public sealed class ExportRunner
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ExportOptions _options;
    private readonly List<string> _warnings = new();

    public ExportRunner(ExportOptions options) => _options = options;

    public int Run()
    {
        var sourceRoot = Git(Directory.GetCurrentDirectory(), "rev-parse --show-toplevel").Trim();
        var configPath = Path.GetFullPath(_options.ConfigPath ?? Path.Combine(sourceRoot, "tools", "TeamExport", "team-export.json"));
        var config = ExportConfig.Load(configPath);
        var target = Path.GetFullPath(_options.TargetDirectory);

        ValidateTarget(sourceRoot, target);
        if (!_options.AllowDirty)
        {
            RequireClean(sourceRoot, "Repo gốc");
            RequireClean(target, "Repo team");
        }

        var sha = Git(sourceRoot, "rev-parse HEAD").Trim();
        Console.WriteLine($"Nguồn : {sourceRoot} @ {sha[..10]}");
        Console.WriteLine($"Đích  : {target}");

        var isExcluded = config.Matcher(config.Exclude);
        var isStripExcluded = config.Matcher(config.StripCommentExclude);
        var isPreserved = config.Matcher(config.PreserveInTarget);
        var stripExtensions = new HashSet<string>(config.StripCommentExtensions, StringComparer.OrdinalIgnoreCase);

        CleanTarget(target, isPreserved);

        var files = Git(sourceRoot, "ls-files -z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int copied = 0, stripped = 0, skipped = 0;
        foreach (var rel in files)
        {
            if (isExcluded(rel))
            {
                skipped++;
                continue;
            }
            var sourcePath = Path.Combine(sourceRoot, rel);
            if (!File.Exists(sourcePath))
                continue; // file đã xoá nhưng chưa commit (chỉ gặp khi --allow-dirty)

            var bytes = File.ReadAllBytes(sourcePath);
            var output = Transform(rel, bytes, config, stripExtensions, isStripExcluded, isExcluded, out var changedByStrip);
            if (changedByStrip)
                stripped++;

            var destPath = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllBytes(destPath, output);
            copied++;
        }

        var overlayCount = CopyOverlay(config, configPath, target);

        Console.WriteLine($"Đã xuất {copied} file ({stripped} file được xoá comment), bỏ qua {skipped} file, overlay {overlayCount} file.");
        foreach (var w in _warnings)
            Console.WriteLine($"  [cảnh báo] {w}");

        if (_options.Build)
        {
            Console.WriteLine("Build thử bản xuất (dotnet build)...");
            var (code, log) = Run("dotnet", "build -v q -nologo", target);
            if (code != 0)
            {
                Console.WriteLine(log);
                Console.Error.WriteLine("Build bản xuất FAIL — không commit. File vẫn nằm ở thư mục đích để xem.");
                return 1;
            }
            Console.WriteLine("Build OK.");
        }

        if (_options.Commit)
            CommitAndPush(target, sha);
        else
            Console.WriteLine("Chưa commit (thêm --commit để commit vào repo team). Xem thay đổi: git -C <đích> status");
        return 0;
    }

    private byte[] Transform(
        string rel, byte[] bytes, ExportConfig config, HashSet<string> stripExtensions,
        Func<string, bool> isStripExcluded, Func<string, bool> isExcluded, out bool changedByStrip)
    {
        changedByStrip = false;
        var ext = Path.GetExtension(rel);
        var json = config.JsonTransforms.GetValueOrDefault(rel);
        var isSln = ext.Equals(".sln", StringComparison.OrdinalIgnoreCase);
        var strip = stripExtensions.Contains(ext) && !isStripExcluded(rel);
        if (json is null && !isSln && !strip)
            return bytes;

        var (text, bom) = Decode(bytes, rel);
        var result = text;
        try
        {
            if (json is not null)
                result = JsonTransform.Apply(result, json, w => _warnings.Add($"{rel}: {w}"));
            if (isSln)
                result = SolutionFilter.Filter(result, isExcluded);
            if (strip)
            {
                var keep = config.KeepCommentsContaining;
                var before = result;
                result = ext.ToLowerInvariant() switch
                {
                    ".cs" => CSharpCommentStripper.Strip(result, keep),
                    ".js" => JavaScriptCommentStripper.Strip(result, keep),
                    ".css" => CssCommentStripper.Strip(result, keep),
                    ".cshtml" or ".razor" => RazorCommentStripper.Strip(result, keep, w => _warnings.Add($"{rel}: {w}")),
                    ".csproj" or ".props" or ".targets" => XmlCommentStripper.Strip(result, keep),
                    _ => throw new InvalidOperationException($"Chưa hỗ trợ xoá comment cho đuôi '{ext}'.")
                };
                changedByStrip = !ReferenceEquals(before, result) && before != result;
            }
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new InvalidOperationException($"Lỗi khi xử lý {rel}: {ex.Message}", ex);
        }
        return Encode(result, bom);
    }

    private static (string Text, bool Bom) Decode(byte[] bytes, string rel)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try
        {
            return (StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException($"{rel} không phải UTF-8.");
        }
    }

    private static byte[] Encode(string text, bool bom)
    {
        var body = StrictUtf8.GetBytes(text);
        return bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    private static void ValidateTarget(string sourceRoot, string target)
    {
        if (!Directory.Exists(Path.Combine(target, ".git")))
            throw new InvalidOperationException(
                $"'{target}' không phải gốc một git repo. Clone repo team về trước, rồi trỏ --target vào đó.");

        var src = sourceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dst = target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (src.StartsWith(dst, StringComparison.OrdinalIgnoreCase) || dst.StartsWith(src, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Thư mục đích không được trùng, nằm trong, hoặc chứa repo gốc.");
    }

    private static void RequireClean(string repo, string label)
    {
        var status = Git(repo, "status --porcelain").Trim();
        if (status.Length > 0)
            throw new InvalidOperationException(
                $"{label} ({repo}) còn thay đổi chưa commit:\n{status}\nCommit/stash trước, hoặc chạy với --allow-dirty.");
    }

    /// <summary>
    /// Xoá các file ĐANG ĐƯỢC GIT THEO DÕI ở repo team (trừ file preserve) để file đã xoá/đổi tên ở repo
    /// gốc cũng biến mất bên team. File không được theo dõi (bin/, obj/...) không bị đụng tới.
    /// </summary>
    private static void CleanTarget(string target, Func<string, bool> isPreserved)
    {
        var tracked = Git(target, "ls-files -z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rel in tracked)
        {
            if (isPreserved(rel))
                continue;
            var path = Path.Combine(target, rel);
            if (File.Exists(path))
                File.Delete(path);
        }
        foreach (var dir in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories)
                     .Where(d => !d.Contains(Path.DirectorySeparatorChar + ".git"))
                     .OrderByDescending(d => d.Length))
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    private static int CopyOverlay(ExportConfig config, string configPath, string target)
    {
        if (string.IsNullOrEmpty(config.OverlayDirectory))
            return 0;
        var overlay = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, config.OverlayDirectory));
        if (!Directory.Exists(overlay))
            return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(overlay, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(overlay, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            count++;
        }
        return count;
    }

    private void CommitAndPush(string target, string sha)
    {
        Git(target, "add -A");
        if (Git(target, "status --porcelain").Trim().Length == 0)
        {
            Console.WriteLine("Repo team đã khớp với bản xuất — không có gì để commit.");
            return;
        }

        // Message để trống: danh sách file trên Bitbucket không hiện mô tả nào ở cột Description.
        Git(target, "commit -q --allow-empty-message -m \"\"");
        Console.WriteLine($"Đã commit vào repo team (nguồn: {sha}):");
        Console.WriteLine(Git(target, "log -1 --stat --format=").TrimEnd());

        if (_options.Push)
        {
            Git(target, "push");
            Console.WriteLine("Đã push.");
        }
        else
        {
            Console.WriteLine($"Chưa push. Kiểm tra rồi chạy: git -C \"{target}\" push");
        }
    }

    private static string Git(string cwd, string args)
    {
        var (code, output) = Run("git", args, cwd);
        if (code != 0)
            throw new InvalidOperationException($"git {args} (tại {cwd}) lỗi:\n{output}");
        return output;
    }

    private static (int Code, string Output) Run(string file, string args, string cwd)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };
        // Đường dẫn tiếng Việt trong output git không bị escape thành \303\241...
        psi.Environment["GIT_CONFIG_PARAMETERS"] = "'core.quotepath=false'";
        using var p = Process.Start(psi)!;
        var stderrTask = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var stderr = stderrTask.Result;
        return (p.ExitCode, stdout + (p.ExitCode != 0 ? stderr : ""));
    }
}
