using System.Text.RegularExpressions;

namespace TeamExport;

/// <summary>
/// Bỏ khỏi file .sln những project nằm trong thư mục không xuất (tests/, tools/...), nếu không thì
/// <c>dotnet build</c> ở repo team fail vì tham chiếu tới project không tồn tại.
/// </summary>
public static partial class SolutionFilter
{
    [GeneratedRegex("^Project\\(\"\\{[^}]+\\}\"\\) = \"[^\"]*\", \"(?<path>[^\"]+)\", \"(?<guid>\\{[^}]+\\})\"")]
    private static partial Regex ProjectLine();

    public static string Filter(string sln, Func<string, bool> isExcluded)
    {
        var newline = sln.Contains("\r\n") ? "\r\n" : "\n";
        var lines = sln.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var removedGuids = new List<string>();
        var output = new List<string>();
        var skipping = false;
        foreach (var line in lines)
        {
            if (skipping)
            {
                if (line.StartsWith("EndProject", StringComparison.Ordinal))
                    skipping = false;
                continue;
            }
            var m = ProjectLine().Match(line);
            if (m.Success && isExcluded(m.Groups["path"].Value.Replace('\\', '/')))
            {
                removedGuids.Add(m.Groups["guid"].Value);
                skipping = true;
                continue;
            }
            output.Add(line);
        }

        // Dòng cấu hình build / lồng thư mục nhắc tới project đã bỏ.
        output = output
            .Where(l => !removedGuids.Any(g => l.Contains(g, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return string.Join(newline, output);
    }
}
