using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Builds;

/// <summary>
/// Một lệnh của cổng biên dịch: đối số truyền LITERAL (không qua shell) + thư mục chạy, dạng đường dẫn
/// tương đối so với gốc workspace — đúng hợp đồng của <c>CommandTools.RunArgs</c>.
/// </summary>
/// <param name="Label">Nhãn hiển thị trong báo cáo/log ("backend (dotnet build)").</param>
/// <param name="Args">Đối số, phần tử đầu là tên file thực thi trần (<c>dotnet</c>, <c>npm</c>).</param>
/// <param name="WorkingDirectory">Thư mục chạy, tương đối so với gốc workspace.</param>
/// <param name="IsInstall">
/// Lệnh cài phụ thuộc (<c>npm install</c>): thất bại ở đây KHÔNG phải lỗi code của agent (thường là
/// mạng/registry), nên cổng build báo là SKIPPED chứ không bắt Developer đi sửa một lỗi không phải của nó.
/// </param>
public record BuildCommand(string Label, IReadOnlyList<string> Args, string WorkingDirectory, bool IsInstall = false);

/// <summary>
/// Suy ra "phải chạy lệnh gì để biết code vừa sinh có biên dịch được không" từ CHÍNH nội dung workspace.
/// <para>
/// Vì sao phải dò thay vì khai cứng: prompt bước Implementation cho agent tự chọn stack trong hai lựa
/// chọn (.NET hoặc Node thuần), còn dự án khung Bosch thì luôn là .NET + Angular. Một danh sách lệnh
/// cứng sẽ hoặc chạy <c>dotnet build</c> trong một cây Node, hoặc bỏ sót hẳn phần frontend.
/// </para>
/// <para>
/// Không dò ra gì ⇒ trả danh sách RỖNG, và cổng build coi đó là "bỏ qua" chứ không phải "trượt":
/// chốt chặn này chỉ được phép chặn khi nó thật sự đo được cái gì đó.
/// </para>
/// </summary>
public static class BuildCommandPlanner
{
    // Độ sâu tìm file dự án tính từ gốc mỗi repo đích. 3 đủ cho cây thường gặp
    // (src/backend/ThuVien.Api/ThuVien.Api.csproj) mà không phải quét cả cây.
    private const int MaxProbeDepth = 3;

    /// <summary>
    /// Dựng kế hoạch build cho các repo đích của dự án (xem <see cref="ProjectRepositoryLayout"/>).
    /// Mỗi slot được dò độc lập nên dự án Bosch ra hai nhóm lệnh (backend .NET + frontend Angular).
    /// </summary>
    public static IReadOnlyList<BuildCommand> Plan(string workspaceRoot, IReadOnlyList<ProjectRepositorySlot> slots)
    {
        var commands = new List<BuildCommand>();
        foreach (var slot in slots)
        {
            var slotPath = Path.Combine(workspaceRoot, slot.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(slotPath))
                continue;

            commands.AddRange(PlanDotnet(workspaceRoot, slotPath, slot.Label));
            commands.AddRange(PlanNode(workspaceRoot, slotPath, slot.Label));
        }

        return commands;
    }

    // .NET: ưu tiên file .sln (một lệnh build phủ mọi project) rồi mới tới .csproj lẻ. Truyền TÊN FILE
    // làm đối số thay vì để dotnet tự dò thư mục: thư mục có hai .csproj thì "dotnet build" trần báo lỗi
    // "found more than one project" — một lỗi hạ tầng đội lốt lỗi biên dịch.
    private static IEnumerable<BuildCommand> PlanDotnet(string workspaceRoot, string slotPath, string label)
    {
        var solution = FindShallowest(slotPath, "*.sln");
        if (solution != null)
        {
            yield return Build(workspaceRoot, solution, label, "dotnet build");
            yield break;
        }

        var project = FindShallowest(slotPath, "*.csproj");
        if (project != null)
            yield return Build(workspaceRoot, project, label, "dotnet build");

        static BuildCommand Build(string workspaceRoot, string file, string label, string title) => new(
            $"{label} ({title})",
            ["dotnet", "build", Path.GetFileName(file), "--nologo"],
            ToRelative(workspaceRoot, Path.GetDirectoryName(file)!));
    }

    // Node: chỉ dựng lệnh khi package.json có script "build" thật. Không có script đó thì chạy
    // "npm install" một mình chẳng chứng minh được gì về code vừa sinh, chỉ tốn vài phút mỗi vòng.
    private static IEnumerable<BuildCommand> PlanNode(string workspaceRoot, string slotPath, string label)
    {
        var packageJson = FindShallowest(slotPath, "package.json");
        if (packageJson == null)
            yield break;

        string content;
        try { content = File.ReadAllText(packageJson); }
        catch (IOException) { yield break; }

        if (!NodeScripts.HasBuildScript(content))
            yield break;

        var workingDirectory = ToRelative(workspaceRoot, Path.GetDirectoryName(packageJson)!);
        // --no-audit --no-fund: hai bước phụ chỉ in báo cáo ra log, không đổi kết quả cài.
        yield return new BuildCommand($"{label} (npm install)", ["npm", "install", "--no-audit", "--no-fund"], workingDirectory, IsInstall: true);
        yield return new BuildCommand($"{label} (npm run build)", ["npm", "run", "build"], workingDirectory);
    }

    // File khớp mẫu NÔNG NHẤT (ít cấp thư mục nhất, rồi tới thứ tự tên) trong giới hạn độ sâu — cây
    // dự án luôn để solution/manifest ở trên, còn các bản sao nằm sâu (thư mục mẫu, test fixture) thì
    // không phải thứ cần build. Bỏ qua node_modules/bin/obj/.git bằng bộ lọc dùng chung.
    private static string? FindShallowest(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(f => !WorkspaceFileFilter.IsInRegenerableDirectory(root, f))
                .Where(f => Depth(root, f) <= MaxProbeDepth)
                .OrderBy(f => Depth(root, f))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int Depth(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;

    // CommandTools nhận thư mục chạy là đường dẫn TƯƠNG ĐỐI so với gốc workspace và giải nó qua đúng
    // chốt chặn path-traversal của các tool file — nên ở đây trả về dạng tương đối, dùng '/' cho khớp
    // với các đường dẫn khác đi vào prompt/log.
    private static string ToRelative(string workspaceRoot, string fullPath) =>
        Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
}
