using ICOGenerator.Services.Requirements.Knowledge;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// BM25 index: xếp đoạn khớp truy vấn lên đầu, tôn trọng filter/boost, và tokenizer xử lý đúng
// tiếng Việt có dấu + stopword. Đây là "bộ não" xếp hạng của tri thức xuyên dự án.
public class Bm25TextIndexTests
{
    private static KnowledgeChunk Chunk(string text, string project = "P", string? orgUnit = null) =>
        new(Guid.NewGuid(), project, orgUnit, "Product Brief", null, text);

    [Fact]
    public void Search_RanksMatchingChunkAboveUnrelated()
    {
        var relevant = Chunk("Ứng dụng quản lý kho vật tư giúp thủ kho theo dõi nhập xuất tồn.");
        var unrelated = Chunk("Theo dõi giờ làm ca đêm nhân viên xưởng lắp ráp.");
        var index = new Bm25TextIndex(new[] { unrelated, relevant });

        var hits = index.Search("quản lý kho vật tư", topK: 2);

        Assert.True(hits.Count >= 1);
        Assert.Same(relevant, hits[0].Chunk);
    }

    [Fact]
    public void Search_QueryWithNoOverlap_ReturnsEmpty()
    {
        var index = new Bm25TextIndex(new[] { Chunk("Ứng dụng quản lý kho vật tư.") });

        Assert.Empty(index.Search("chấm công nhân sự", topK: 5));
        Assert.Empty(index.Search("", topK: 5));
    }

    [Fact]
    public void Search_FilterExcludesChunks_BeforeScoring()
    {
        var mine = Chunk("Quản lý kho vật tư của tôi.");
        var other = Chunk("Quản lý kho vật tư của người khác.");
        var index = new Bm25TextIndex(new[] { mine, other });

        var hits = index.Search("quản lý kho vật tư", topK: 5, filter: c => !ReferenceEquals(c, mine));

        Assert.All(hits, h => Assert.NotSame(mine, h.Chunk));
        Assert.Contains(hits, h => ReferenceEquals(h.Chunk, other));
    }

    [Fact]
    public void Search_BoostLiftsEquallyRelevantChunk()
    {
        // Hai đoạn giống hệt nội dung ⇒ điểm gốc bằng nhau; boost phải quyết định thứ hạng.
        var boosted = Chunk("Ứng dụng quản lý kho vật tư cho phân xưởng.", orgUnit: "HcP/MFW");
        var plain = Chunk("Ứng dụng quản lý kho vật tư cho phân xưởng.");
        var index = new Bm25TextIndex(new[] { plain, boosted });

        var hits = index.Search("quản lý kho vật tư", topK: 2,
            boost: c => c.OrgUnitCode == "HcP/MFW" ? 1.25 : 1.0);

        Assert.Equal(2, hits.Count);
        Assert.Same(boosted, hits[0].Chunk);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void Search_HeadingTermsCountTowardScore()
    {
        var withHeading = new KnowledgeChunk(Guid.NewGuid(), "P", null, "BRD", "Luồng duyệt phiếu xuất",
            "Nhân viên tạo phiếu rồi chuyển lên cấp trên xử lý tiếp theo quy trình nội bộ.");
        var index = new Bm25TextIndex(new[] { withHeading });

        Assert.Single(index.Search("luồng duyệt phiếu xuất", topK: 1));
    }

    [Fact]
    public void Tokenize_LowercasesKeepsDiacritics_DropsStopwordsAndShortTokens()
    {
        var tokens = Bm25TextIndex.Tokenize("Quản LÝ kho, và của A-Z09!");

        Assert.Contains("quản", tokens);
        Assert.Contains("lý", tokens);
        Assert.Contains("kho", tokens);
        Assert.Contains("z09", tokens);
        Assert.DoesNotContain("và", tokens);   // stopword
        Assert.DoesNotContain("của", tokens);  // stopword
        Assert.DoesNotContain("a", tokens);    // < 2 ký tự
    }
}
