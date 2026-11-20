using ICOGenerator.Localization;
using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Workflows;

/// <summary>Kết cục của cổng kịch bản nghiệm thu cho một lượt dựng/sửa POC.</summary>
public enum PocUatGateStatus
{
    /// <summary>Mọi kịch bản UAT đều đạt (có thể sau vài vòng tự sửa).</summary>
    Passed,

    /// <summary>Hết ngạch tự sửa mà vẫn còn kịch bản chưa đạt.</summary>
    StillFailing,

    /// <summary>Không đo được: chưa có bản demo, không có bộ UAT, hoặc trình duyệt headless không chạy.</summary>
    Unverified
}

/// <param name="RoundsUsed">Số vòng tự sửa đã chạy (0 = đạt ngay lần kiểm đầu, hoặc không đo được).</param>
/// <param name="UatTotal">Số kịch bản UAT của dự án (0 khi không đo được).</param>
/// <param name="RemainingFailures">Kịch bản còn gãy ở lần kiểm CUỐI — rỗng khi <see cref="PocUatGateStatus.Passed"/>.</param>
/// <param name="SkipReason">Vì sao không đo được — chỉ có khi <see cref="PocUatGateStatus.Unverified"/>.</param>
public sealed record PocUatGateOutcome(
    PocUatGateStatus Status,
    int RoundsUsed,
    int UatTotal,
    IReadOnlyList<string> RemainingFailures,
    string? SkipReason = null);

/// <summary>
/// CỔNG KỊCH BẢN NGHIỆM THU của bước POC: bản demo chỉ tới tay người dùng sau khi MÁY đã tự chạy hết bộ
/// kịch bản UAT trên nó và tất cả đều đạt — còn kịch bản gãy thì tự giao lại Developer sửa rồi kiểm lại,
/// lặp tới khi sạch hoặc hết <see cref="DeliveryPipeline.MaxPocUatFixRounds"/> vòng.
///
/// <para>
/// Vì sao phải là CODE chạy chứ không phải prompt nhắc: prompt POC đã bảo agent tự audit tối đa 3 vòng và
/// sửa cho sạch, nhưng agent được phép dừng khi "vòng cuối vẫn còn issue không xử lý được" — và trước cổng
/// này, câu trả lời cuối của nó là thứ duy nhất quyết định bản demo đi tiếp. Kết quả: người dùng mở trang
/// POC Review, bấm thử đúng một kịch bản trong hướng dẫn sử dụng, thấy nó gãy, ghim ghi chú, gửi đi chờ
/// một vòng sửa — cho một lỗi máy đã biết từ trước khi họ mở trang. Cùng lý do với cổng biên dịch quanh
/// bước Implementation.
/// </para>
/// <para>
/// Thước là <see cref="PocVerificationSummary.UatIssues"/> của MỘT lượt audit do chính cổng gọi (không tin
/// lần audit cuối của agent: agent có thể sửa tiếp sau lần đó, hoặc không audit). Chỉ bộ UAT, không gồm luồng
/// end-to-end agent tự đặt thêm: bộ UAT là thứ người dùng sẽ bấm thử, luồng kia không ai ngoài agent thấy.
/// </para>
/// <para>
/// Hết ngạch thì KHÔNG hủy run — cùng luật với cổng biên dịch: bản demo đã dựng vẫn có giá trị, người duyệt
/// là người quyết. Chỉ phải nói thật là nó còn gãy (mốc "error" trên feed + mục trong bàn giao). Fail-open
/// khi không đo được: môi trường không có Chromium không được biến thành vòng lặp sửa mù.
/// </para>
/// </summary>
public static class PocUatGate
{
    /// <summary>Đọc kết quả một lượt audit thành kết luận của cổng. Hàm thuần — test được không cần browser.</summary>
    public static PocUatGateOutcome Evaluate(PocVerificationSummary? summary, int roundsUsed = 0)
    {
        if (summary == null)
            return Unverified(roundsUsed, Loc.T("PocUatGate.NoDemo"));

        if (summary.UatTotal == 0)
            return Unverified(roundsUsed, Loc.T("PocUatGate.NoScenarios"));

        // Không có runtime thì không biết kịch bản nào chạy được — kể cả khi tầng tĩnh đã thấy thiếu
        // pocScenarios(): sửa xong cũng không có cách nào xác nhận, nên không mở vòng sửa nào.
        if (!summary.RuntimeRan)
            return Unverified(roundsUsed, summary.RuntimeSkipReason ?? Loc.T("PocUatGate.NoRuntime"));

        return summary.UatIssues.Count == 0
            ? new PocUatGateOutcome(PocUatGateStatus.Passed, roundsUsed, summary.UatTotal, Array.Empty<string>())
            : new PocUatGateOutcome(PocUatGateStatus.StillFailing, roundsUsed, summary.UatTotal, summary.UatIssues.ToList());
    }

    /// <summary>
    /// Chạy cổng: kiểm → còn gãy và còn ngạch thì sửa → kiểm lại. Hai việc nặng (audit trong trình duyệt,
    /// lượt agent sửa) đi qua delegate để vòng điều phối test được bằng fake.
    /// </summary>
    /// <param name="auditAsync">Một lượt audit chốt (xem <c>WorkspaceTools.AuditPocForGateAsync</c>).</param>
    /// <param name="fixAsync">Một vòng sửa: (số vòng, các kịch bản đang gãy) — sản phẩm nằm trên đĩa, nên vòng
    /// sửa không trả gì; lần audit kế tiếp là thứ duy nhất nói nó đã sửa được gì.</param>
    /// <param name="report">Mốc tiến độ cho feed của run (kind, message, detail).</param>
    public static async Task<PocUatGateOutcome> EnforceAsync(
        Func<CancellationToken, Task<PocVerificationSummary?>> auditAsync,
        Func<int, IReadOnlyList<string>, CancellationToken, Task> fixAsync,
        int maxRounds,
        Action<string, string, string?> report,
        CancellationToken cancellationToken)
    {
        report("tool", Loc.T("PocUatGate.Checking"), null);

        var round = 0;
        while (true)
        {
            PocVerificationSummary? summary;
            try
            {
                summary = await auditAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cổng hỏng KHÁC demo hỏng: lỗi hạ tầng của cổng không được giữ bản demo lại.
                var broken = Unverified(round, ex.Message);
                report("info", Loc.T("PocUatGate.Skipped", broken.SkipReason), null);
                return broken;
            }

            var outcome = Evaluate(summary, round);
            switch (outcome.Status)
            {
                case PocUatGateStatus.Unverified:
                    report("info", Loc.T("PocUatGate.Skipped", outcome.SkipReason), null);
                    return outcome;

                case PocUatGateStatus.Passed:
                    report("tool", round == 0
                        ? Loc.T("PocUatGate.Passed", outcome.UatTotal)
                        : Loc.T("PocUatGate.PassedAfterFix", outcome.UatTotal, round), null);
                    return outcome;
            }

            var failing = outcome.RemainingFailures;
            if (round >= maxRounds)
            {
                report("error", Loc.T("PocUatGate.GaveUp", failing.Count, outcome.UatTotal, round),
                    string.Join(Environment.NewLine, failing));
                return outcome;
            }

            round++;
            report("tool", Loc.T("PocUatGate.Fixing", failing.Count, outcome.UatTotal, round, maxRounds),
                string.Join(Environment.NewLine, failing));

            try
            {
                await fixAsync(round, failing, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Vòng sửa hỏng (model lỗi, hết ngân sách…) không xoá được bản demo đã dựng. Không thử thêm
                // vòng nào nữa — lỗi kiểu này hiếm khi tự hết ở lần gọi kế — nhưng vẫn chấm lại một lần:
                // vòng sửa có thể đã kịp ghi một phần bản vá xuống đĩa.
                report("info", Loc.T("PocUatGate.FixFailed", round), ex.Message);
                maxRounds = round;
            }
        }
    }

    /// <summary>
    /// Danh sách kịch bản đang gãy cho prompt vòng sửa (<c>Developer/poc-uat-fix.v1.md</c>). Chữ gửi cho MODEL
    /// nên không đi qua <c>Loc</c>.
    /// </summary>
    public static string FormatFailures(IReadOnlyList<string> failures) =>
        string.Join(Environment.NewLine, failures.Select((f, i) => $"{i + 1}. {f}"));

    /// <summary>
    /// Mục bàn giao nối vào <c>AgentTask.Output</c> của task POC: người duyệt ở Agent Dashboard và vòng
    /// "Yêu cầu chỉnh sửa" kế tiếp (đọc output này làm bàn giao lần trước) đều phải biết bản demo đã qua cổng
    /// chưa. Kết cục <see cref="PocUatGateStatus.Unverified"/> không thêm gì: không có gì để nói ngoài feed.
    /// </summary>
    public static string? BuildHandoffSection(PocUatGateOutcome outcome, IReadOnlyList<string> fixHandoffs)
    {
        if (outcome.Status == PocUatGateStatus.Unverified)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Loc.T("PocUatGate.Handoff.Heading"));
        sb.AppendLine();
        sb.AppendLine(outcome.Status == PocUatGateStatus.Passed
            ? Loc.T("PocUatGate.Handoff.Passed", outcome.UatTotal, outcome.RoundsUsed)
            : Loc.T("PocUatGate.Handoff.StillFailing", outcome.RemainingFailures.Count, outcome.UatTotal, outcome.RoundsUsed));

        foreach (var failure in outcome.RemainingFailures)
            sb.AppendLine("- " + failure);

        for (var i = 0; i < fixHandoffs.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(fixHandoffs[i]))
                continue;
            sb.AppendLine();
            sb.AppendLine(Loc.T("PocUatGate.Handoff.Round", i + 1));
            sb.AppendLine();
            sb.AppendLine(fixHandoffs[i].Trim());
        }

        return sb.ToString().TrimEnd();
    }

    private static PocUatGateOutcome Unverified(int roundsUsed, string? reason) =>
        new(PocUatGateStatus.Unverified, roundsUsed, 0, Array.Empty<string>(), reason);
}
