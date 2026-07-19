namespace ICOGenerator.Domain.Enums;

// Loại tài liệu nguồn người dùng upload cho project để agent BA tham khảo khi viết requirement.
// Spreadsheet (Excel .xlsx / .csv): bảng người dùng nghiệp vụ hay dùng — được bóc thành text có cấu
// trúc (tiêu đề cột + vài dòng mẫu) thay vì ảnh chụp làm mất cấu trúc. Xem ProjectSourceIngestor.
public enum SourceFileKind { Image = 1, Pdf = 2, Spreadsheet = 3 }
