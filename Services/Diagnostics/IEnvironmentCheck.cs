namespace ICOGenerator.Services.Diagnostics;

/// <summary>
/// Một nhóm kiểm tra của nút "Test Environment" (trang Settings). Thêm nhóm mới = thêm một class hiện thực
/// interface này + một dòng đăng ký DI — <see cref="EnvironmentDiagnostics"/> tự nhặt, không phải sửa gì khác.
/// <para>
/// Luật cho người viết check: KHÔNG ném lỗi cho kết luận bình thường (trả <see cref="EnvironmentCheckStatus.Fail"/>
/// kèm <c>Fix</c>); exception lọt ra được runner đổi thành một dòng Fail chung. Không sửa trạng thái của app
/// (không ghi DB, không đổi cấu hình) — người vận hành bấm nút này bao nhiêu lần cũng được.
/// </para>
/// </summary>
public interface IEnvironmentCheck
{
    /// <summary>Tên nhóm hiển thị.</summary>
    string Group { get; }

    /// <summary>Trần thời gian của cả nhóm; quá trần thì runner trả một dòng Fail "timeout" cho nhóm.</summary>
    TimeSpan Timeout { get; }

    Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken);
}
