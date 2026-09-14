using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Quy tắc TẤT ĐỊNH trả lời đúng một câu: MỘT lời gọi model được phép mang bao nhiêu token prompt, đo
/// bằng <see cref="TokenEstimator"/>.
/// <para>
/// Vì sao KHÔNG lấy phần trăm của <see cref="AiModel.ContextWindow"/>: context window là giới hạn KỸ
/// THUẬT (vượt thì lời gọi hỏng), còn thứ phải canh là giới hạn KINH TẾ. Với gpt-5.6-luna, prompt vượt
/// <see cref="LongContextPriceCliffTokens"/> bị tính <b>2x giá input và 1,5x giá output cho TOÀN BỘ
/// request</b> — một bậc thang, không phải cái dốc: vượt 1 token thì cả prompt đổi giá. Neo trần vào
/// phần trăm context window (1.050.000) sẽ đặt trần ở 420.000, tức nằm sâu trong vùng giá đôi và càng
/// đổi sang model context lớn thì càng đắt. Nên trần là số TUYỆT ĐỐI, còn context window chỉ làm cận
/// trên an toàn cho model nhỏ.
/// </para>
/// <para>
/// KHÔNG còn hệ số an toàn nào ở đây, và đó là điểm dễ hiểu nhầm nhất nếu sửa sau này:
/// <see cref="TokenEstimator"/> nay ĐẾM bằng tokenizer thật chứ không ước lượng theo số ký tự, nên trần
/// dưới đây so trực tiếp với vách giá. Hệ số 5/8 cũ tồn tại để bù cho giả định "4 ký tự/token" — mà giả
/// định đó lệch hai chiều tùy ngôn ngữ (xem <see cref="TokenEstimator"/>), nên nhân thêm một hằng số chỉ
/// đúng cho tiếng Việt và siết nhầm khối tài liệu nguồn tiếng Anh xuống ~43% mức đáng có. Đừng thêm lại
/// một hệ số nào vào đây: sai số còn lại là do model dùng bảng mã khác o200k, và nó không phải một tỉ lệ
/// cố định để bù.
/// </para>
/// </summary>
public static class PromptBudget
{
    /// <summary>
    /// Vách giá long-context của gpt-5.6-luna: prompt &gt; 272K token ⇒ 2x input, 1,5x output cho cả
    /// request. Xem docs/llm-and-prompts.md.
    /// </summary>
    public const int LongContextPriceCliffTokens = 272_000;

    /// <summary>Phần chừa cho output (model reasoning tính cả token suy luận ẩn vào đây).</summary>
    public const int OutputReserveTokens = 32_000;

    /// <summary>Trần tối thiểu — model context tí hon vẫn phải gửi được một lượt có nghĩa.</summary>
    public const int MinimumPromptTokens = 2_000;

    /// <summary>Trần TỔNG prompt của một lời gọi, đo bằng <see cref="TokenEstimator"/>.</summary>
    public static int Resolve(AiModel model)
    {
        var ceiling = model.ContextWindow > 0
            ? Math.Min(model.ContextWindow, LongContextPriceCliffTokens)
            : LongContextPriceCliffTokens;

        // Model có context nhỏ hơn cả phần chừa output thì lấy một nửa thay vì ra số âm.
        var usable = Math.Max(ceiling / 2, ceiling - OutputReserveTokens);
        return Math.Max(MinimumPromptTokens, usable);
    }

    /// <summary>
    /// Phần trần dành cho HỘI THOẠI nguyên văn. Một phần ba: ba khối co giãn của prompt chat BA là
    /// prompt nền cố định, text tài liệu nguồn, và hội thoại — chia đều để một khối phình không bóp chết
    /// hai khối kia. Với gpt-5.6-luna ⇒ 80.000 token.
    /// </summary>
    public static int ConversationTokens(AiModel model) => Resolve(model) / 3;

    /// <summary>
    /// Phần trần dành cho TEXT tài liệu nguồn, cộng dồn trên mọi nguồn của project. Trần mỗi file
    /// (<c>Llm:SourceUpload:MaxTextCharsPerFile</c>) không chặn được tổng: mười file đủ 20.000 ký tự là
    /// 34.000–68.000 token chỉ riêng phần nguồn, tùy tài liệu tiếng Anh hay tiếng Việt.
    /// </summary>
    public static int SourceTokens(AiModel model) => Resolve(model) / 3;

    /// <summary>
    /// Phần trần dành cho ẢNH tài liệu nguồn, cộng dồn trên mọi nguồn của MỘT lời gọi, đo bằng
    /// <see cref="TokenEstimator.EstimateImage(int,int)"/>. Một PHẦN SÁU trần: ảnh nằm trong khối nguồn
    /// nhưng hai khối chữ đã lấy trọn 2/3, nên ảnh ăn vào NỬA của khối còn lại — phần khối đó không dùng
    /// hết cho prompt nền. Với gpt-5.6-luna ⇒ 40.000 token, thừa sức chứa trần
    /// <c>Llm:SourceUpload:MaxImagesPerCall</c> mặc định (12 ảnh chụp màn hình full-width ≈ 17.000).
    /// <para>
    /// Vì sao ảnh vẫn cần trần RIÊNG chứ không trừ chung vào <see cref="SourceTokens"/>: KHÔNG còn vì hai
    /// bên đo bằng hai thước khác nhau — từ khi <see cref="TokenEstimator"/> đếm bằng tokenizer thật thì
    /// chữ và ảnh đã chung một thước. Lý do bây giờ là SÀN BẢO ĐẢM: gộp một quỹ thì một project nhiều tài
    /// liệu chữ sẽ ăn hết phần của ảnh, mà cắt ảnh là thứ đắt nhất trong cả prompt nếu cắt nhầm — BA mất
    /// luôn nội dung không có ở đâu khác (sơ đồ, ảnh chụp màn hình Excel). Quỹ riêng giữ cho hai loại
    /// nguồn không giết nhau.
    /// </para>
    /// </summary>
    public static int ImageTokens(AiModel model) => Resolve(model) / 6;
}
