using Microsoft.ML.Tokenizers;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Đếm token của phần CHỮ bằng tokenizer THẬT (<c>o200k_base</c> — bảng mã của dòng GPT-4o/5 và là
/// tokenizer gần nhất với đa số endpoint OpenAI-compatible mà app trỏ tới), và ước lượng phần ẢNH bằng
/// công thức chia ô của provider.
/// <para>
/// Vì sao KHÔNG còn là "~4 ký tự/token": heuristic đó lệch theo HAI CHIỀU tùy nội dung, nên không một hệ
/// số bù nào sửa được. Đo trên chính loại nội dung app này gửi đi: tiếng Việt có dấu 3,60 ký tự/token
/// (len/4 ra 0,90x), tiếng Anh 5,88 (len/4 ra <b>1,47x — THỪA</b>), HTML/JS của poc-demo 3,40 (0,84x),
/// JSON call log 2,93 (0,73x). Một prompt chat BA trộn cả bốn loại. Hệ số bù cũ (5/8 ở
/// <see cref="PromptBudget"/>) giả định lệch đồng nhất 1,6 lần THIẾU, nên với khối tài liệu nguồn tiếng
/// Anh nó siết ngân sách xuống còn ~43% mức đáng có — cắt mất tài liệu mà BA không có nguồn nào khác.
/// </para>
/// <para>
/// Phần ẢNH không đo bằng tokenizer được — xem <see cref="EstimateImage(int,int)"/>. Ước lượng của một lời
/// gọi CÓ ảnh phải cộng cả hai phần lại, nếu không mọi con số suy ra từ đây (trần prompt, chi phí khi
/// endpoint không trả <c>usage</c>) đều coi như ảnh không tồn tại.
/// </para>
/// <para>
/// Vẫn là ƯỚC LƯỢNG với model không dùng o200k (DeepSeek, model tự host): bảng mã khác thì số khác. Nhưng
/// nó sai theo NGÔN NGỮ đúng cách, thay vì sai một hệ số cố định — và số THẬT vẫn về đúng ngay khi
/// endpoint trả <c>usage</c>, chỗ này chỉ đỡ cho lúc nó không trả.
/// </para>
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// Tokenizer dùng chung. <c>Lazy</c> vì nạp bảng vocab (~200k mục) tốn vài chục ms và app có thể chạy
    /// cả phiên không đụng tới — nhưng nạp MỘT lần rồi thôi: đây là đường nóng của mọi lời gọi model
    /// (69k ký tự ⇒ 9ms sau khi đã nạp).
    /// <para>
    /// Fail-safe về heuristic cũ nếu gói dữ liệu vocab vắng mặt lúc chạy: đếm token là thứ chạy TRƯỚC mọi
    /// lời gọi model, nên một gói thiếu ở môi trường lạ sẽ làm chết cả app thay vì làm sai một con số.
    /// </para>
    /// </summary>
    private static readonly Lazy<Tokenizer?> Tokenizer = new(() =>
    {
        try
        {
            return TiktokenTokenizer.CreateForEncoding(EncodingName);
        }
        catch (Exception)
        {
            return null;
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private const string EncodingName = "o200k_base";

    /// <summary>Số ký tự trên một token khi phải dùng đường lùi — xem <see cref="Tokenizer"/>.</summary>
    private const int FallbackCharsPerToken = 4;

    /// <summary>Ô vuông mà provider chia ảnh ra để tính tiền, và giá của mỗi ô.</summary>
    private const int TilePixels = 512;
    private const int TokensPerTile = 170;

    /// <summary>Phần cố định mỗi ảnh phải trả, bất kể to nhỏ.</summary>
    private const int ImageBaseTokens = 85;

    /// <summary>Ảnh bị thu về vừa khung này trước khi chia ô.</summary>
    private const int MaxImageSide = 2048;

    /// <summary>Sau khi vừa khung, cạnh NGẮN còn bị thu tiếp về mức này (chỉ thu nhỏ, không phóng to).</summary>
    private const int ShortSideTarget = 768;

    /// <summary>
    /// Mức tính cho ảnh KHÔNG đọc được kích thước: đúng bằng một ảnh vuông 1024px, tức
    /// <c>EstimateImage(1024, 1024)</c> (một test khóa lại đẳng thức này để hằng số không trôi khi công
    /// thức đổi).
    /// <para>
    /// Vì sao không suy từ số byte: cùng một khung hình, ảnh PNG chụp màn hình và ảnh JPEG nén mạnh chênh
    /// nhau hàng chục lần dung lượng mà số token y hệt nhau — quy đổi theo byte là đoán sai có hệ thống.
    /// Vì sao không phải 0: 0 chính là cái lỗi cơ chế này sinh ra để sửa, và một định dạng lạ không phải
    /// lý do để ảnh đi lậu vé.
    /// </para>
    /// </summary>
    public const int UnknownImageTokens = 765;

    /// <summary>
    /// Số token của một khối chữ. Chuỗi rỗng/toàn khoảng trắng ⇒ 0; mọi chuỗi có nội dung ⇒ ít nhất 1.
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var tokenizer = Tokenizer.Value;
        return tokenizer == null
            ? Math.Max(1, text.Length / FallbackCharsPerToken)
            : Math.Max(1, tokenizer.CountTokens(text));
    }

    /// <summary>
    /// Token của MỘT ảnh gửi kèm prompt, theo cách các provider vision tính tiền: thu ảnh về vừa khung
    /// 2048px, thu tiếp cạnh ngắn về 768px, chia thành các ô 512px rồi tính
    /// <see cref="ImageBaseTokens"/> + <see cref="TokensPerTile"/> mỗi ô. Ảnh 1024×1024 ⇒ 765 token,
    /// 1024×1536 ⇒ 1.105 token.
    /// <para>
    /// ĐÂY LÀ ƯỚC LƯỢNG, và cố ý dùng chung một công thức cho mọi model: từng provider có bảng riêng
    /// (thậm chí thu phí theo "ảnh nhỏ/vừa/lớn"), nhưng cùng bậc độ lớn — và số THẬT vẫn về đúng ngay khi
    /// endpoint trả <c>usage</c>, chỗ này chỉ đỡ cho lúc nó không trả. Đo trật vài chục phần trăm vẫn hơn
    /// đứt việc coi một lượt 12 ảnh là 0 token.
    /// </para>
    /// </summary>
    public static int EstimateImage(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return UnknownImageTokens;

        var (w, h) = ShrinkToFit(width, height, MaxImageSide);

        // CHỈ thu nhỏ. Phóng một icon 32px lên 768px để "chuẩn hóa" là bắt nó trả tiền như một ảnh chụp
        // màn hình — sai chiều đúng ở loại ảnh nhiều nhất trong tài liệu (logo, con dấu, icon trong Word).
        var shortSide = Math.Min(w, h);
        if (shortSide > ShortSideTarget)
            (w, h) = Scale(w, h, (double)ShortSideTarget / shortSide);

        var tiles = CeilDiv(w, TilePixels) * CeilDiv(h, TilePixels);
        return ImageBaseTokens + TokensPerTile * tiles;
    }

    /// <summary>
    /// Token của một ảnh khi trong tay chỉ có bytes. Đọc kích thước từ header
    /// (<see cref="ImageDimensions"/>); không đọc được thì <see cref="UnknownImageTokens"/>.
    /// </summary>
    public static int EstimateImage(ReadOnlySpan<byte> imageBytes)
    {
        var size = ImageDimensions.Read(imageBytes);
        return size is { } s ? EstimateImage(s.Width, s.Height) : UnknownImageTokens;
    }

    private static (int Width, int Height) ShrinkToFit(int width, int height, int maxSide)
    {
        var longest = Math.Max(width, height);
        return longest <= maxSide ? (width, height) : Scale(width, height, (double)maxSide / longest);
    }

    // Làm tròn LÊN: một cạnh 769px co xuống 512,67 vẫn chiếm sang ô thứ hai, làm tròn xuống là mất trắng một ô.
    private static (int Width, int Height) Scale(int width, int height, double factor) =>
        (Math.Max(1, (int)Math.Ceiling(width * factor)), Math.Max(1, (int)Math.Ceiling(height * factor)));

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;
}
