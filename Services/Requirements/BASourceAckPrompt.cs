namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Khối lệnh dựng từ DỮ LIỆU cho lượt BA đọc tài liệu nguồn vừa upload
/// (<see cref="BAChatService.AcknowledgeSourcesAsync"/>). Phần luật viết bản đọc lại — giọng, nhịp, cụm
/// "Chỗ chưa chắc" — nằm ở prompt <c>source-ack.v3.md</c>, đo được ở Prompt Evals và sửa được ở Prompt
/// Studio; ở đây chỉ có những điều prompt KHÔNG có cách nào tự biết: file nào đang chờ chốt bảng cột, và
/// file nào thật sự vừa được gửi ở lượt này.
/// </summary>
public static class BASourceAckPrompt
{
    /// <summary>
    /// Khối chọn HÌNH DẠNG của lượt đọc file, dựng từ dữ liệu chứ không để model tự đoán.
    ///
    /// <para>
    /// Còn bảng tính chưa chốt cột ⇒ lượt này chỉ bày BẢNG CỘT kèm một lời giới thiệu ngắn. Bản đọc lại chi
    /// tiết của bảng tính (và cụm "Chỗ chưa chắc" của nó) bị đẩy sang lượt sau — lượt mà người dùng đã chốt
    /// xong phạm vi cột — vì kể lại cả file trước khi biết họ dùng cột nào là: dựng việc tồn trên những cột
    /// sắp bị bỏ tích, đọc nhầm cả file khi người dùng gửi nhầm file, và bày ra một bức tường chữ ngay trên
    /// đúng cái bảng chở cùng nội dung ở dạng sửa được. Xem <c>source-readback.v1.md</c> cho nửa còn lại.
    /// </para>
    ///
    /// <para>
    /// Khối này mang thêm PHẠM VI KỂ LẠI — các file thật sự vừa gửi ở lượt này. Lượt đọc file nạp lại toàn
    /// bộ nguồn của project, còn model thì không có cách nào tự phân biệt file mới với file đã xác nhận từ
    /// lượt trước, nên thiếu dòng này nó kể lại tất — xem <see cref="ReadbackScope"/>.
    /// </para>
    /// </summary>
    public static string TurnShape(
        IReadOnlyList<string> pendingColumnFiles,
        IReadOnlyList<string> justSentFiles,
        IReadOnlyList<string> earlierFiles)
    {
        var shape = pendingColumnFiles.Count > 0
            ? "## LƯỢT NÀY: CHỐT PHẠM VI CỘT (bắt buộc)\n"
              + "File bảng tính CHƯA chốt bảng cột: " + string.Join(", ", pendingColumnFiles) + ".\n"
              + "Với các file đó, lượt này CHỈ làm hai việc: điền `columns` phủ đủ mọi cột của file (ý nghĩa "
              + "viết sẵn, tích sẵn cột nghiệp vụ), và viết `message` NGẮN — tối đa năm câu: file này là gì, "
              + "quy mô thật, rồi mời người dùng rà bảng bên dưới và bấm \"Gửi bảng cột\".\n"
              + "TUYỆT ĐỐI KHÔNG kể lại chi tiết từng cột và KHÔNG viết cụm \"Chỗ chưa chắc\" cho các file "
              + "đó: bản đọc lại của bảng tính là lượt SAU, sau khi người dùng chốt xong cột. Ngoại lệ duy "
              + "nhất là MỘT câu khi file rõ ràng không phải thứ bạn vừa xin — họ cần biết ngay để gửi lại.\n"
              + "Nguồn khác trong cùng lô (Word/PDF/ảnh) vẫn được đọc lại đầy đủ như thường."
            : "## LƯỢT NÀY: BẢN ĐỌC LẠI\n"
              + "Không có file bảng tính nào đang chờ chốt bảng cột ⇒ `columns` là mảng RỖNG, và lượt này là "
              + "bản đọc lại đầy đủ: kể lại thứ bạn đọc được, nêu cụm \"Chỗ chưa chắc\", kết bằng câu hỏi "
              + "đóng để người dùng bấm một trong hai chip.";

        return shape + ReadbackScope(justSentFiles, earlierFiles);
    }

    /// <summary>
    /// PHẠM VI KỂ LẠI của lượt đọc file: gọi đích danh các file VỪA GỬI, và nói thẳng rằng các nguồn cũ chỉ
    /// đính kèm để đối chiếu.
    ///
    /// <para>
    /// Ca thật đã gặp: người dùng đã chốt bảng cột cho một file Excel từ đầu buổi, nhiều lượt sau gửi thêm
    /// một ảnh chụp biểu mẫu để trả lời một câu hỏi — và BA kể lại CẢ HAI, mở đầu lượt bằng gần nửa số dòng
    /// nói lại đúng bộ cột người dùng đã tích tay ở lượt trước. Model không sai luật nào nó được cho: nó
    /// thấy text của mọi nguồn nằm dưới cùng một câu "tôi vừa đính kèm", và prompt bắt "MỌI file vừa gửi
    /// đều phải được nhắc tới". Chỗ hỏng là cơ chế nói dối về chữ "vừa gửi", nên chỗ vá cũng phải ở đây —
    /// prompt không có cách nào tự biết file nào mới.
    /// </para>
    ///
    /// <para>
    /// Nguồn cũ vẫn ĐI KÈM, và ngoại lệ cho phép nhắc tên chúng là cố ý: điểm chưa rõ đắt nhất của một lô
    /// upload thường nằm đúng ở chỗ NỐI giữa file mới và file cũ (biểu mẫu vừa gửi lấy người học từ file
    /// danh sách hay tự nhập?). Cấm nhắc tên là cắt luôn câu hỏi đó.
    /// </para>
    /// </summary>
    public static string ReadbackScope(IReadOnlyList<string> justSentFiles, IReadOnlyList<string> earlierFiles)
    {
        if (justSentFiles.Count == 0)
            return string.Empty;

        var scope = "\n\n## PHẠM VI KỂ LẠI CỦA LƯỢT NÀY\n"
            + "File người dùng VỪA GỬI ở lượt này: " + string.Join(", ", justSentFiles) + ". Chỉ những file "
            + "này mới là thứ lượt này phải kể lại và xin xác nhận.\n";

        if (earlierFiles.Count == 0)
            return scope;

        return scope
            + "Các nguồn còn lại — " + string.Join(", ", earlierFiles) + " — đã gửi từ TRƯỚC và người dùng đã "
            + "xác nhận cách bạn hiểu chúng rồi; chúng đính kèm ở đây CHỈ để bạn đối chiếu. TUYỆT ĐỐI KHÔNG "
            + "kể lại chúng: không mô tả lại nội dung/cột/quy mô của chúng, không dựng cụm \"Chỗ chưa chắc\" "
            + "cho riêng chúng. Kể lại một file đã được xác nhận là bắt người dùng đọc và duyệt lần thứ hai "
            + "đúng thứ họ vừa duyệt, trong khi file họ thật sự vừa gửi bị đẩy xuống nửa dưới của lượt.\n"
            + "Được phép nhắc tên chúng đúng MỘT trường hợp: nêu một điểm chưa rõ nằm ở chỗ NỐI giữa file "
            + "vừa gửi và chúng (dữ liệu bên này lấy từ bên kia hay nhập tay?). Đó là câu hỏi chỉ lộ ra khi "
            + "đặt hai nguồn cạnh nhau, và nó thuộc về file vừa gửi.";
    }
}
