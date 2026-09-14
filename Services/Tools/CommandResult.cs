namespace ICOGenerator.Services.Tools;

/// <summary>
/// Kết quả một lần chạy lệnh, dạng CÓ CẤU TRÚC.
/// <para>
/// Các tool trả về <see cref="Output"/> dưới dạng chuỗi observation cho model đọc, nhưng cổng biên dịch
/// (<see cref="Builds.ImplementationBuildVerifier"/>) là code quyết định chứ không phải model: nó cần
/// biết lệnh THÀNH CÔNG hay không. Suy điều đó bằng cách dò chuỗi "ExitCode: 0" trong khối text là một
/// mối nối compiler không kiểm được — đổi định dạng khối là cổng build âm thầm cho qua mọi lỗi.
/// </para>
/// </summary>
/// <param name="ExitCode">Mã thoát của tiến trình; <c>-1</c> cho các trường hợp không chạy được (bị chặn, timeout).</param>
/// <param name="Output">Khối text đúng như tool vẫn trả cho model.</param>
public record CommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}
