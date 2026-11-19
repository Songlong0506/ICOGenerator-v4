using TeamExport;

const string usage = """
Xuất ICOGenerator-v4 sang repo của team (xem docs/team-export.md).

Cách dùng (chạy từ thư mục repo ICOGenerator-v4):
  dotnet run --project tools/TeamExport -- --target <thư mục clone repo team> [tuỳ chọn]

Tuỳ chọn:
  --target <dir>   Bản clone repo team (bắt buộc). File git đang theo dõi ở đó sẽ bị thay bằng bản xuất.
  --commit         Commit vào repo team sau khi build OK.
  --push           Push sau khi commit (kèm --commit).
  --no-build       Bỏ bước dotnet build bản xuất.
  --allow-dirty    Cho phép chạy khi repo gốc/repo team còn thay đổi chưa commit.
  --config <file>  File cấu hình khác tools/TeamExport/team-export.json.
""";

string? target = null, config = null;
bool build = true, commit = false, push = false, allowDirty = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--target" when i + 1 < args.Length: target = args[++i]; break;
        case "--config" when i + 1 < args.Length: config = args[++i]; break;
        case "--commit": commit = true; break;
        case "--push": push = commit = true; break;
        case "--no-build": build = false; break;
        case "--allow-dirty": allowDirty = true; break;
        case "-h" or "--help": Console.WriteLine(usage); return 0;
        default:
            Console.Error.WriteLine($"Tham số không hợp lệ: {args[i]}\n\n{usage}");
            return 2;
    }
}
if (target is null)
{
    Console.Error.WriteLine(usage);
    return 2;
}

try
{
    return new ExportRunner(new ExportOptions(target, config, build, commit, push, allowDirty)).Run();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"LỖI: {ex.Message}");
    return 1;
}
