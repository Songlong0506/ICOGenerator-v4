using System.ComponentModel;

namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Hình dạng JSON của "Ví dụ đã xác nhận" — format LƯU TRỮ trên <c>Project.WorkedExamples</c>.
/// <para>
/// <b>Vì sao cũng là JSON dù mỗi mục CHỈ có một trường.</b> Cột này đi cùng
/// <see cref="OpenQuestionDocument"/> ở mọi chặng: cùng một lời gọi LLM chắt ra
/// (<c>InterviewOutlookService</c>), cùng một con trỏ lượt, cùng ghi trong một
/// <c>SaveChangesAsync</c>. Để một cột JSON còn cột kia bullet là bắt mọi người đọc phải nhớ hai format
/// cho hai thứ luôn xuất hiện cạnh nhau — và nhớ rằng chỉ MỘT trong hai cắt được giữa chừng an toàn
/// (xem <c>InterviewOutlookParser</c>). Đồng bộ ở đây rẻ hơn hẳn cái giá đó.
/// </para>
/// <para>
/// Mục vẫn là chuỗi phẳng chứ KHÔNG tách thành <c>{input, expected}</c>: không tầng nào đọc hai vế
/// riêng. Oracle chấm POC (<c>PocWorkedExampleOracle</c>) bóc <c>WE-n</c> / kỳ vọng từ mục
/// <c>## 13. Worked Examples</c> của AI Design Spec, không từ cột này — cột này chỉ được nạp NGUYÊN
/// khối vào prompt sinh spec. Tách trường mà không có người đọc là dựng thêm một chỗ để trôi lệch.
/// </para>
/// </summary>
public class WorkedExampleDocument
{
    [Description("Mỗi mục là một ví dụ ĐẦU VÀO CỤ THỂ → KẾT QUẢ KỲ VỌNG người dùng đã xác nhận. Chưa chốt ví dụ nào ⇒ mảng rỗng.")]
    public List<string> Items { get; set; } = new();
}
