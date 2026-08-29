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
/// HỆ SỐ AN TOÀN (chỗ dễ làm hỏng nhất nếu sửa sau này): <see cref="TokenEstimator"/> giả định 4 ký
/// tự/token — tỉ lệ của tiếng Anh. Tài liệu và prompt trong repo này là tiếng Việt có dấu, tokenize ra
/// nhiều token hơn hẳn (~2,5 ký tự/token), nên số nó trả về ƯỚC LƯỢNG THIẾU khoảng 1,6 lần. Trần suy từ
/// nó phải nhân 5/8 trước khi so với giới hạn thật, nếu không bộ đếm báo 180.000 trong khi đã vượt vách
/// 272.000 từ lâu.
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

    /// <summary>Trần TỔNG prompt của một lời gọi, tính theo token ước lượng của <see cref="TokenEstimator"/>.</summary>
    public static int Resolve(AiModel model)
    {
        var ceiling = model.ContextWindow > 0
            ? Math.Min(model.ContextWindow, LongContextPriceCliffTokens)
            : LongContextPriceCliffTokens;

        // Model có context nhỏ hơn cả phần chừa output thì lấy một nửa thay vì ra số âm.
        var usable = Math.Max(ceiling / 2, ceiling - OutputReserveTokens);
        return Math.Max(MinimumPromptTokens, usable * 5 / 8);
    }

    /// <summary>
    /// Phần trần dành cho HỘI THOẠI nguyên văn. Một phần ba: ba khối co giãn của prompt chat BA là
    /// prompt nền cố định (~26K ước lượng), text tài liệu nguồn, và hội thoại — chia đều để một khối
    /// phình không bóp chết hai khối kia. Với gpt-5.6-luna ⇒ 50.000 token ước lượng.
    /// </summary>
    public static int ConversationTokens(AiModel model) => Resolve(model) / 3;

    /// <summary>
    /// Phần trần dành cho TEXT tài liệu nguồn, cộng dồn trên mọi nguồn của project. Trần mỗi file
    /// (<c>Llm:SourceUpload:MaxTextCharsPerFile</c>) không chặn được tổng: mười file đủ 20.000 ký tự là
    /// 50.000 token ước lượng chỉ riêng phần nguồn.
    /// </summary>
    public static int SourceTokens(AiModel model) => Resolve(model) / 3;
}
