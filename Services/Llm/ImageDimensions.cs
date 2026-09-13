namespace ICOGenerator.Services.Llm;

/// <summary>
/// Đọc KÍCH THƯỚC (px) của một ảnh từ phần HEADER của file — không giải mã pixel, không kéo thêm thư viện
/// ảnh nào vào dự án.
/// <para>
/// Vì sao tự đọc thay vì dùng ImageSharp/SkiaSharp: chỗ duy nhất cần con số này là
/// <see cref="TokenEstimator.EstimateImage(int,int)"/> — nó chỉ cần chiều rộng/cao để chia ô, và mọi định
/// dạng ảnh gửi được cho model đều ghi hai số đó trong vài chục byte đầu. Thêm một gói giải mã ảnh (kèm
/// native binary theo từng nền tảng) để đọc hai số nguyên là cái giá không đáng, nhất là khi lời gọi này
/// nằm trên đường nóng của MỌI lời gọi model.
/// </para>
/// <para>
/// Không nhận ra định dạng, hoặc header cụt/hỏng ⇒ trả <c>null</c>; nơi gọi phải có đường lùi chứ không
/// được coi ảnh đó là không tồn tại (xem <see cref="TokenEstimator.UnknownImageTokens"/>).
/// </para>
/// </summary>
public static class ImageDimensions
{
    /// <summary>Kích thước (px) của ảnh, hoặc <c>null</c> nếu không đọc được từ header.</summary>
    public static (int Width, int Height)? Read(ReadOnlySpan<byte> bytes)
    {
        var size = ReadRaw(bytes);
        // Header hỏng có thể cho ra số 0 hoặc số âm; một ảnh "0 px" sẽ lặng lẽ thành 0 token, đúng cái lỗi
        // mà cả cơ chế này sinh ra để sửa.
        return size is { Width: > 0, Height: > 0 } ? size : null;
    }

    private static (int Width, int Height)? ReadRaw(ReadOnlySpan<byte> b) =>
        IsPng(b) ? (Be32(b, 16), Be32(b, 20))
        : IsGif(b) ? (Le16(b, 6), Le16(b, 8))
        : IsBmp(b) ? (Math.Abs(Le32(b, 18)), Math.Abs(Le32(b, 22)))
        : IsWebp(b) ? Webp(b)
        : IsJpeg(b) ? Jpeg(b)
        : null;

    // PNG: chữ ký 8 byte rồi chunk IHDR — rộng/cao là hai số big-endian ở offset cố định 16 và 20.
    private static bool IsPng(ReadOnlySpan<byte> b) =>
        b.Length >= 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G'
        && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    private static bool IsGif(ReadOnlySpan<byte> b) =>
        b.Length >= 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8';

    private static bool IsBmp(ReadOnlySpan<byte> b) =>
        b.Length >= 26 && b[0] == 'B' && b[1] == 'M';

    private static bool IsJpeg(ReadOnlySpan<byte> b) =>
        b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8;

    private static bool IsWebp(ReadOnlySpan<byte> b) =>
        b.Length >= 16 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
        && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';

    // WebP có BA biến thể chứa kích thước ở ba chỗ khác nhau; đọc nhầm biến thể ra số rác nên phải khớp
    // đúng chữ ký của từng loại chứ không đoán theo độ dài.
    private static (int Width, int Height)? Webp(ReadOnlySpan<byte> b)
    {
        // Lossy (VP8): khung key-frame mở đầu bằng 3 byte 9D 01 2A, rộng/cao là 14 bit thấp của hai số 16 bit.
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ')
            return b.Length >= 30 && b[23] == 0x9D && b[24] == 0x01 && b[25] == 0x2A
                ? (Le16(b, 26) & 0x3FFF, Le16(b, 28) & 0x3FFF)
                : null;

        // Lossless (VP8L): 4 byte sau chữ ký 0x2F gói (rộng-1) 14 bit rồi (cao-1) 14 bit.
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L')
        {
            if (b.Length < 25 || b[20] != 0x2F)
                return null;
            var bits = Le32(b, 21);
            return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }

        // Mở rộng (VP8X): kích thước KHUNG là hai số 24 bit (đã trừ 1), nằm ngay sau cờ tính năng.
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
            return b.Length >= 30 ? (Le24(b, 24) + 1, Le24(b, 27) + 1) : null;

        return null;
    }

    // JPEG không có offset cố định: kích thước nằm trong khung SOFn, và trước nó là bao nhiêu khung metadata
    // (EXIF, ICC, thumbnail…) thì tùy máy chụp. Phải đi lần theo chuỗi khung.
    private static (int Width, int Height)? Jpeg(ReadOnlySpan<byte> b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            // Khung nào cũng mở đầu bằng 0xFF; byte đệm 0xFF thừa là hợp lệ nên trượt qua từng byte một.
            if (b[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = b[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            // Các marker KHÔNG có phần thân: SOI, TEM, và 8 marker restart.
            if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }

            var length = Be16(b, i + 2);
            if (length < 2)
                return null;

            // SOF0–SOFF là mọi kiểu nén (baseline/progressive/lossless/arithmetic) và tất cả đều xếp cao
            // trước rộng ở cùng offset. Ba marker xen giữa dải đó KHÔNG phải SOF: DHT (C4), JPG (C8), DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                return (Be16(b, i + 7), Be16(b, i + 5));

            i += 2 + length;
        }

        return null;
    }

    private static int Be32(ReadOnlySpan<byte> b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    private static int Be16(ReadOnlySpan<byte> b, int i) => (b[i] << 8) | b[i + 1];
    private static int Le32(ReadOnlySpan<byte> b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
    private static int Le24(ReadOnlySpan<byte> b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
    private static int Le16(ReadOnlySpan<byte> b, int i) => b[i] | (b[i + 1] << 8);
}
