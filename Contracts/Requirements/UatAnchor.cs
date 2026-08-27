namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// NEO CHỈ CHỖ của một bước kịch bản nghiệm thu: mã <c>"{kịch bản}.{bước}"</c> (1-based) mà agent dựng
/// POC gắn thẳng lên phần tử — <c>&lt;button data-uat="2.3"&gt;</c> — cho bước thứ 3 của kịch bản thứ 2.
///
/// <para>
/// Vì sao có lớp này thay vì để mỗi nơi tự nối chuỗi: mã neo phải GIỐNG HỆT NHAU ở bốn chỗ do bốn tầng
/// khác nhau sinh ra — khối prompt giao đích cho agent (<see cref="Services.Requirements.UatScenarioService"/>),
/// thuộc tính agent viết vào HTML, cổng đối chiếu tĩnh (<c>PocUatAnchors</c>) và lượt lái thật/tô sáng
/// khi review. Lệch một quy ước đánh số ở bất kỳ đâu là cả cơ chế im lặng hỏng: agent gắn 0-based còn
/// trang review tìm 1-based thì không neo nào khớp, mà chẳng tầng nào báo lỗi.
/// </para>
///
/// <para>
/// Neo đi theo CHỈ SỐ GỐC trong <see cref="UatScenarioSet.Scenarios"/> — cùng chỉ số mà trang POC Review
/// dùng làm <c>data-index</c> và khóa tick localStorage — nên thứ tự hiển thị đổi cũng không làm lệch neo.
/// </para>
/// </summary>
public static class UatAnchor
{
    /// <summary>Tên thuộc tính mang neo trên phần tử POC.</summary>
    public const string Attribute = "data-uat";

    /// <summary>
    /// Mã neo của một bước. Cả hai tham số là chỉ số 0-based; mã in ra 1-based để trùng với cách con
    /// người (và khối prompt) đánh số "kịch bản 2, bước 3".
    /// </summary>
    public static string Token(int scenarioIndex, int stepIndex) => $"{scenarioIndex + 1}.{stepIndex + 1}";

    /// <summary>
    /// Thuộc tính đầy đủ để nêu trong prompt — agent chép nguyên văn vào phần tử.
    /// </summary>
    public static string Markup(int scenarioIndex, int stepIndex) => $"{Attribute}=\"{Token(scenarioIndex, stepIndex)}\"";
}
