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
        Assert.StartsWith("Cột A | Cột B", text); // không còn ký tự BOM lẫn vào ô đầu
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
