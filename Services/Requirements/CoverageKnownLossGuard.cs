using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH chống <b>mất trắng phần đã ghi nhận</b> của một dòng: một dòng vừa nhận là mình đã
/// biết điều gì đó (<c>[RÕ]</c>/<c>[MỘT PHẦN]</c>) vừa không ghi lại được điều gì cả là một dòng tự mâu
/// thuẫn — và cái nó nuốt mất là nội dung của cả buổi phỏng vấn, không phải một ô trình bày.
///
/// <para>
/// <b>Vì sao cần, sau khi <c>known</c> thành danh sách.</b> Danh sách chở TRẠNG THÁI MỚI NHẤT, nên model
/// được phép XOÁ một mẩu mà người dùng đã đính chính — đó là chủ ý, và nó là thứ giữ cho bản đồ không
/// biến thành nhật ký "A, rồi sửa thành B, rồi sửa thành C". Nhưng cùng cái quyền ấy mở ra một đường
/// hỏng mà bản cũ (một ô chuỗi bị ghi đè) không có: một lượt chắt lọc trả về mảng RỖNG cho một dòng đang
/// đầy — vì model tóm tắt hụt, vì nó hiểu "chỉ giữ điều mới nhất" thành "chỉ giữ lượt mới nhất" — thì
/// không có lỗi nào được ném ra, không ai thấy gì, chỉ có tiến độ khai thác lặng lẽ mất một nhóm.
/// </para>
///
/// <para>
/// <b>Chỉ đỡ đúng ca mất TRẮNG.</b> Guard không so từng mẩu và không cấm model rút gọn: xoá bớt, viết
/// lại, gộp hai mẩu làm một đều là việc hợp lệ của lượt chắt lọc, và một guard đi đếm số mẩu sẽ chặn
/// đúng thứ người dùng vừa yêu cầu (đính chính thì phải xoá được mẩu cũ). Nó chỉ bắt trạng thái KHÔNG
/// THỂ ĐÚNG: còn nhận là biết mà không còn giữ chữ nào. Ca ấy thì phần đã ghi nhận CŨ được trả lại
/// nguyên vẹn — bản cũ chắc chắn đúng hơn một ô trống, và lượt chắt lọc kế tiếp vẫn được sửa tiếp.
/// </para>
///
/// <para>
/// Dòng chuyển sang <c>[CHƯA HỎI]</c>/<c>[KHÔNG ÁP DỤNG]</c> thì KHÔNG đụng tới: rỗng ở hai trạng thái đó
/// là đúng định nghĩa (và <c>[KHÔNG ÁP DỤNG]</c> có lý do riêng thường được ghi vào chính <c>known</c>,
/// nên guard chỉ trả lại khi model để trống hẳn).
/// </para>
/// </summary>
public static class CoverageKnownLossGuard
{
    /// <summary>
    /// Trả lại phần đã ghi nhận CŨ cho những dòng vừa bị xoá trắng mà vẫn đứng ở <c>[RÕ]</c>/<c>[MỘT PHẦN]</c>.
    /// </summary>
    /// <param name="items">Bản đồ vừa chắt ra, sửa TẠI CHỖ.</param>
    /// <param name="previous">Bản đồ đang lưu trước lượt này (đọc từ <c>Project.RequirementCoverageMap</c>).</param>
    public static void Apply(IReadOnlyList<CoverageMapItem> items, IReadOnlyList<CoverageMapItem> previous)
    {
        if (items.Count == 0 || previous.Count == 0)
            return;

        foreach (var item in items)
        {
            if (item.Known.Count > 0)
                continue;

            if (item.Status is not ("RÕ" or "MỘT PHẦN"))
                continue;

            // Khớp theo NHÃN như mọi tầng nối hai danh sách khác của bản đồ — thứ tự 12 dòng là luật cho
            // model chứ không phải bảo đảm, và một lượt trả về lệch thứ tự thì so theo vị trí là trả nhầm
            // phần đã ghi nhận của nhóm này cho nhóm khác.
            var old = previous.FirstOrDefault(x => CoverageMapParser.IsSameGroup(x.Label, item.Label));
            if (old == null || old.Known.Count == 0)
                continue;

            item.Known = old.Known.ToList();
        }
    }
}
