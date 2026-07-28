using ICOGenerator.Domain;

namespace ICOGenerator.Data;

// Dữ liệu mẫu đồng bộ từ HR_Portal (bảng Associates) — giữ nguyên Id/giá trị gốc để khớp với dữ liệu
// thật bên HR_Portal. Nội dung nằm ở Data/SeedData/associates.ndjson (nhúng vào assembly), không còn
// là mảng C# viết tay — xem SeedDataResource để biết lý do.
//
// Load() đọc theo yêu cầu thay vì phơi ra `static readonly` như trước: seed chỉ chạy một lần lúc khởi
// động, xong thì mảng được GC thu hồi thay vì nằm lại trong bộ nhớ suốt vòng đời tiến trình.
public static class AssociatesSeedData
{
    internal const string ResourceName = "ICOGenerator.Data.SeedData.associates.ndjson";

    public static Associate[] Load() => SeedDataResource.Load<Associate>(ResourceName);
}
