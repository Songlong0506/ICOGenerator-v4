using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

public class SpreadsheetTextExtractorTests
{
    [Theory]
    [InlineData("data.xlsx", null, true)]
    [InlineData("data.csv", "text/plain", true)]                 // .csv nhận theo đuôi dù contentType lạ
    [InlineData("book.xlsm", null, true)]
    [InlineData("photo.png", "image/png", false)]
    [InlineData("doc.pdf", "application/pdf", false)]
    [InlineData("noext", "text/csv", true)]                      // nhận theo contentType text/csv
    public void IsSpreadsheet(string fileName, string? contentType, bool expected)
    {
        Assert.Equal(expected, SpreadsheetTextExtractor.IsSpreadsheet(contentType, fileName));
    }

    [Fact]
    public void ExtractCsv_PreservesHeadersAndRows_RespectingQuotes()
    {
        var csv = "Tên,Phòng ban,Lương\r\n"
                + "Nguyễn Văn A,Kỹ thuật,\"1,000\"\r\n"
                + "Trần Thị B,\"Kế toán, Tài chính\",2000\r\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(csv);

        var text = SpreadsheetTextExtractor.Extract(bytes, "luong.csv");

        Assert.NotNull(text);
        Assert.Contains("Tên | Phòng ban | Lương", text);
        Assert.Contains("Nguyễn Văn A | Kỹ thuật | 1,000", text);       // dấu phẩy trong ô có nháy được giữ
        Assert.Contains("Trần Thị B | Kế toán, Tài chính | 2000", text); // ô có phẩy trong nháy không bị tách
    }

    [Fact]
    public void ExtractCsv_StripsUtf8Bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes("Cột A,Cột B\n1,2")).ToArray();

        var text = SpreadsheetTextExtractor.Extract(bytes, "x.csv");

        Assert.NotNull(text);
        // Không còn ký tự BOM lẫn vào ô đầu — soi đúng dòng tiêu đề, vì trước nó còn khối "Tổng: …".
        Assert.Contains(text!.Split('\n').Select(l => l.Trim()), l => l == "Cột A | Cột B");
        Assert.DoesNotContain('﻿', text!);
    }

    [Fact]
    public void ExtractXlsx_ReadsSheetNameHeadersAndSharedStrings()
    {
        var bytes = BuildXlsx("Nhân viên",
            new[] { "Tên", "Phòng", "Điểm" },
            new[] { "An", "Kỹ thuật", "80" });

        var text = SpreadsheetTextExtractor.Extract(bytes, "nhan-vien.xlsx");

        Assert.NotNull(text);
        Assert.Contains("Sheet: Nhân viên", text);
        Assert.Contains("Tên | Phòng | Điểm", text);
        Assert.Contains("An | Kỹ thuật | 80", text);
    }

    [Fact]
    public void Extract_ReturnsNullForGarbage()
    {
        Assert.Null(SpreadsheetTextExtractor.Extract(new byte[] { 1, 2, 3, 4 }, "broken.xlsx"));
    }

    // Danh mục của một cột phải tính trên CẢ bảng, không phải trên mấy chục dòng đầu.
    //
    // Ca thật (file kế hoạch đào tạo 262 dòng): 29 dòng đầu chỉ chứa Assignment Type "REQ"/"MAN" nên bản
    // đọc lại của BA bỏ sót "OPT" — đúng giá trị mã hóa "khóa học TỰ CHỌN" mà người dùng đã nói ngay câu
    // đầu tiên. Cùng cửa sổ đó, cột Required Date trống sạch trong khi phía dưới có dòng mang hạn hoàn
    // thành, nên bản đọc kết luận cột này "đang để trống" rồi dựng một mục "Chỗ chưa chắc" trên tiền đề sai.
    [Fact]
    public void ExtractXlsx_ProfilesEveryColumnOverTheWholeSheet_NotJustTheSampleRows()
    {
        var rows = new List<string[]> { new[] { "Nhân viên", "Loại", "Hạn" } };
        for (var i = 0; i < 40; i++)                                   // 40 dòng đầu: chỉ REQ, Hạn trống
            rows.Add(new[] { $"NV{i}", "REQ", "" });
        rows.Add(new[] { "NV40", "OPT", "31/Dec/2025" });              // dòng thứ 41 — ngoài cửa sổ mẫu

        var text = SpreadsheetTextExtractor.Extract(BuildXlsx("KH", rows.ToArray()), "kh.xlsx")!;

        // OPT nằm ngoài các dòng mẫu nhưng vẫn phải có mặt trong thống kê, kèm số dòng.
        Assert.Contains("Loại: có giá trị 41/41 · ĐỦ 2 giá trị: REQ (40), OPT (1)", text);
        // Cột thưa không được phép bị kể thành "để trống".
        Assert.Contains("Hạn: có giá trị 1/41", text);
        // Và bản đọc phải được cảnh báo rằng phần bảng chỉ là dòng đầu.
        Assert.Contains(SpreadsheetTextExtractor.DataRowsHeading, text);
    }

    // SourceContextBuilder cắt text mỗi nguồn ở MaxTextCharsPerFile và GIỮ PHẦN ĐẦU, nên thứ đặt cuối là
    // thứ rơi ra trước. Danh mục cột không suy lại được từ dòng mẫu, còn dòng mẫu thì mất vài dòng vẫn còn
    // dùng được — nên thống kê phải đứng trước, kể cả với bảng rộng ăn hết ngân sách bằng vài chục dòng.
    [Fact]
    public void ExtractXlsx_PutsColumnStatsBeforeSampleRows_SoTruncationDropsTheCheaperHalf()
    {
        var rows = new List<string[]> { new[] { "Mã", "Loại" } };
        for (var i = 0; i < 35; i++)
            rows.Add(new[] { $"M{i}", i % 2 == 0 ? "A" : "B" });

        var text = SpreadsheetTextExtractor.Extract(BuildXlsx("S", rows.ToArray()), "s.xlsx")!;

        Assert.True(text.IndexOf(SpreadsheetTextExtractor.ColumnStatsHeading, StringComparison.Ordinal)
                    < text.IndexOf(SpreadsheetTextExtractor.DataRowsHeading, StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractXlsx_FlagsColumnsThatCarryNoInformation()
    {
        var text = SpreadsheetTextExtractor.Extract(BuildXlsx("S",
            new[] { "Tên", "Tên đệm", "Đang dùng" },
            new[] { "An", "", "Yes" },
            new[] { "Bình", "", "Yes" })!, "s.xlsx")!;

        Assert.Contains("Tên đệm: TRỐNG ở toàn bộ 2 dòng", text);
        Assert.Contains("Đang dùng: có giá trị 2/2 · CHỈ MỘT giá trị duy nhất: Yes", text);
    }

    // OpenXML BỎ HẲN phần tử của ô rỗng, nên đọc tuần tự row.Elements<Cell>() thì mọi ô sau một chỗ trống
    // bị trượt sang trái. Ca thật: dòng thiếu ô "Active User" làm tên người rơi vào cột đó, và bản đọc lại
    // của BA đi báo với người dùng rằng "dữ liệu bị lệch giữa hàng tiêu đề và các giá trị" — bảng gốc không
    // lệch chút nào, chỉ phần text ta dựng ra mới lệch. Người dùng không có cách nào phân xử được điều đó.
    [Fact]
    public void ExtractXlsx_PlacesCellsByReference_SoSparseRowsDoNotShiftColumns()
    {
        var bytes = BuildXlsxWithGaps();

        var text = SpreadsheetTextExtractor.Extract(bytes, "thua.xlsx")!;

        // Dòng thiếu ô ở cột B: giá trị cột C phải ở lại cột C, không trượt vào chỗ của B.
        Assert.Contains("An |  | Kỹ thuật", text);
        Assert.Contains("Phòng: có giá trị 1/1", text);
        Assert.Contains("Đang dùng: TRỐNG ở toàn bộ 1 dòng", text);
    }

    // Dựng .xlsx có ô rỗng bị LƯỢC BỎ (đúng cách Excel xuất file thật): hàng dữ liệu chỉ có ô A2 và C2.
    private static byte[] BuildXlsxWithGaps()
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            static Cell Text(string reference, string value) => new()
            {
                CellReference = reference,
                DataType = CellValues.String,
                CellValue = new CellValue(value)
            };

            var sheetData = new SheetData(
                new Row(Text("A1", "Tên"), Text("B1", "Đang dùng"), Text("C1", "Phòng")),
                new Row(Text("A2", "An"), Text("C2", "Kỹ thuật")));   // B2 bị lược bỏ

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            workbookPart.Workbook.AppendChild(new Sheets()).AppendChild(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Thưa"
            });
            workbookPart.Workbook.Save();
        }
        return ms.ToArray();
    }

    // Dựng một .xlsx tối giản (mọi ô là shared string) để test bám đúng đường đọc SharedStringTable.
    private static byte[] BuildXlsx(string sheetName, params string[][] rows)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var sharedPart = workbookPart.AddNewPart<SharedStringTablePart>();
            sharedPart.SharedStringTable = new SharedStringTable();
            var index = new Dictionary<string, int>();
            int Intern(string s)
            {
                if (index.TryGetValue(s, out var i)) return i;
                sharedPart.SharedStringTable.AppendChild(new SharedStringItem(new Text(s)));
                index[s] = index.Count;
                return index[s];
            }

            var sheetData = new SheetData();
            foreach (var row in rows)
            {
                var r = new Row();
                foreach (var cell in row)
                    r.AppendChild(new Cell { DataType = CellValues.SharedString, CellValue = new CellValue(Intern(cell).ToString()) });
                sheetData.AppendChild(r);
            }

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.AppendChild(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = sheetName
            });
            workbookPart.Workbook.Save();
        }
        return ms.ToArray();
    }
}
