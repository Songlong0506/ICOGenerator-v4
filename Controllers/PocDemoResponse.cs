using ICOGenerator.Services.Artifacts;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

/// <summary>
/// Cách DUY NHẤT phục vụ file <c>poc-demo.html</c> ra trình duyệt — dùng chung cho đường CÓ đăng nhập
/// (<see cref="ProjectsController"/>) và đường LINK CHIA SẺ cho khách (<see cref="PocShareController"/>).
///
/// <para>
/// Gộp một chỗ vì đây là RÀO CHẮN BẢO MẬT, không phải tiện ích trình bày: hai đường từng giữ hai bản sao
/// của cùng một header CSP, nên siết rào ở bản này mà quên bản kia sẽ để đường khách chạy tiếp bằng luật
/// cũ — đúng thứ không ai nhận ra khi đọc diff.
/// </para>
/// </summary>
internal static class PocDemoResponse
{
    // HTML này do agent/LLM sinh ra nhưng được phục vụ từ CHÍNH origin của app. Sandbox để mọi <script>
    // bị tiêm chạy trong origin mờ — không đọc được cookie đăng nhập của admin, không POST same-origin đã
    // xác thực (vd tới Settings) — chặn đường leo thang của prompt injection. 'allow-scripts' giữ demo
    // tương tác được; 'allow-forms'/'allow-modals' cho POC gửi form và dùng confirm()/alert().
    // 'allow-same-origin' CỐ Ý không có — chính sự vắng mặt đó là ranh giới bảo mật, và forms/modals
    // không làm nó yếu đi.
    private const string SandboxCsp = "sandbox allow-scripts allow-forms allow-modals;";

    /// <summary>
    /// Đọc file, cắt khối hướng dẫn dành cho agent, (tùy chọn) tiêm annotator rồi trả về kèm CSP sandbox.
    /// </summary>
    /// <param name="review">
    /// Chế độ REVIEW (nhúng trong iframe của trang PocReview): tiêm annotator để người xem ghim ghi chú lên
    /// phần tử. Annotator chỉ nói chuyện với trang cha qua postMessage nên KHÔNG nới rào chắn nào.
    /// </param>
    public static async Task<IActionResult> BuildAsync(
        ControllerBase controller, string filePath, bool review, CancellationToken cancellationToken)
    {
        // poc-demo.html mở đầu bằng một khối comment hướng dẫn agent Developer, chép từ poc-template.html.
        // Đó là chỉ dẫn cho LLM chứ không phải nội dung trang, và một bản chép bị xô lệch sẽ hiện ra dạng
        // chữ thô "(POC_SCRIPT_START/END) holds ONE …" thay vì POC (lỗi "bấm Mockup ra trang hỏng"). Cắt
        // trước khi phục vụ để trình duyệt luôn nhận được vỏ + nội dung, kể cả với demo sinh trước bản vá.
        // File là HTML tự chứa và nhỏ nên đọc trọn vào bộ nhớ (thay vì stream file vật lý) là đủ.
        var html = await File.ReadAllTextAsync(filePath, cancellationToken);
        html = PocTemplate.StripDeveloperGuide(html);

        if (review)
            html = PocTemplate.InjectAnnotator(html);

        controller.Response.Headers["Content-Security-Policy"] = SandboxCsp;
        return controller.Content(html, "text/html; charset=utf-8");
    }
}
