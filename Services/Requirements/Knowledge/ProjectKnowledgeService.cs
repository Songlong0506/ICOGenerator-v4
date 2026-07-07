using System.Text;
using System.Text.RegularExpressions;
using ICOGenerator.Data;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ICOGenerator.Services.Requirements.Knowledge;

/// <summary>
/// Tri thức xuyên dự án (retrieval-augmented): truy xuất các đoạn LIÊN QUAN từ tài liệu ĐÃ DUYỆT của
/// các dự án KHÁC rồi render thành một khối ngữ cảnh (~4KB) đính vào prompt BA — cả lượt chat lẫn bước
/// soạn Product Brief. Nhờ đó BA "nhớ" cách tổ chức từng viết yêu cầu tương tự: hỏi sắc hơn, thống nhất
/// thuật ngữ, và nhận diện trùng lặp ("phòng X từng làm tool gần giống").
///
/// Truy xuất là lexical BM25 thuần in-memory (xem <see cref="Bm25TextIndex"/>) — không thêm hạ tầng
/// embedding, chạy được cả SqlServer lẫn Sqlite. Chỉ mục dựng từ DB rồi cache theo tiến trình
/// (IMemoryCache, hết hạn theo thời gian — tài liệu duyệt mới xuất hiện trong tri thức trễ nhất vài
/// phút). Dự án cùng đơn vị yêu cầu (OrgUnitCode) được cộng điểm nhẹ vì bối cảnh nghiệp vụ gần nhất.
///
/// Nguyên tắc: khối tri thức CHỈ là ngữ cảnh tham khảo — prompt template nói rõ KHÔNG được chép yêu
/// cầu/tính năng từ dự án khác; mọi yêu cầu phải đến từ hội thoại. Fail-open toàn tuyến: chưa có tài
/// liệu duyệt/lỗi DB/lỗi render ⇒ trả null, mọi luồng chạy như khi chưa có tính năng này.
/// </summary>
public partial class ProjectKnowledgeService
{
    private const string TemplatePath = "BA/project-knowledge.v1.md";
    private const string CacheKey = "ProjectKnowledge.Index";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    // 4 đoạn × ~1200 ký tự + phần chữ tĩnh của template ⇒ khối ngữ cảnh cỡ ~4-5KB, cùng cỡ với khối
    // bối cảnh tổ chức — đủ giàu để tham khảo mà không phình prompt.
    private const int TopChunks = 4;
    private const int MaxTotalChars = 4200;
    // Cộng điểm nhẹ cho dự án cùng đơn vị yêu cầu: đủ để thắng khi hai đoạn ngang nhau, không đủ để
    // một đoạn lạc đề cùng phòng đè một đoạn rất khớp khác phòng.
    private const double SameOrgUnitBoost = 1.25;
    // Truy vấn dài (transcript) chỉ lấy phần đầu — nơi mô tả bài toán gốc đậm đặc nhất.
    private const int MaxQueryChars = 4000;

    // Corpus: các tài liệu NGHIỆP VỤ (không AIDesignSpec — spec kỹ thuật cho POC, ít giá trị tham khảo
    // khi khai thác yêu cầu). Key = FileName trong ProjectDocuments, value = nhãn hiển thị trong khối.
    private static readonly string[] CorpusFileNames =
        ["ProductBrief.docx", "BRD.docx", "SRS.docx", "FSD.docx", "UserStories.docx"];

    private static readonly Dictionary<string, string> DocumentLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ProductBrief.docx"] = "Product Brief",
        ["BRD.docx"] = "BRD",
        ["SRS.docx"] = "SRS",
        ["FSD.docx"] = "FSD",
        ["UserStories.docx"] = "User Stories"
    };

    private readonly AppDbContext _db;
    private readonly PromptTemplateService _prompts;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ProjectKnowledgeService> _logger;

    public ProjectKnowledgeService(
        AppDbContext db,
        PromptTemplateService prompts,
        IMemoryCache cache,
        ILogger<ProjectKnowledgeService> logger)
    {
        _db = db;
        _prompts = prompts;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Khối tri thức cho một lượt hỏi của dự án <paramref name="projectId"/>: truy vấn = tên + mô tả
    /// dự án + <paramref name="query"/> (tin nhắn user hoặc transcript). Trả null khi không có đoạn
    /// nào liên quan hoặc khi bất kỳ khâu nào lỗi (fail-open).
    /// </summary>
    public virtual async Task<string?> BuildKnowledgeContextAsync(
        Guid projectId,
        string? projectName,
        string? projectDescription,
        string? projectOrgUnitCode,
        string query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var hits = await SearchAsync(projectId, projectName, projectDescription, projectOrgUnitCode,
                query, TopChunks, cancellationToken);
            return hits.Count == 0 ? null : Render(hits);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không dựng được khối tri thức xuyên dự án — tiếp tục không có phần ngữ cảnh này.");
            return null;
        }
    }

    /// <summary>
    /// Các dự án KHÁC giống dự án đang xét nhất (cho panel "Dự án tương tự" ở trang Requirements):
    /// gom các đoạn khớp theo dự án nguồn, điểm dự án = tổng điểm đoạn, kèm loại tài liệu khớp và một
    /// snippet của đoạn khớp nhất. Fail-open: lỗi/không có gì ⇒ danh sách rỗng.
    /// </summary>
    public virtual async Task<IReadOnlyList<SimilarProject>> FindSimilarProjectsAsync(
        Guid projectId,
        string? projectName,
        string? projectDescription,
        string? projectOrgUnitCode,
        string query,
        int maxProjects = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Lấy dư đoạn (mỗi dự án có thể khớp nhiều đoạn) rồi mới gom nhóm theo dự án.
            var hits = await SearchAsync(projectId, projectName, projectDescription, projectOrgUnitCode,
                query, maxProjects * 4, cancellationToken);

            return hits
                .GroupBy(h => h.Chunk.ProjectId)
                .Select(g =>
                {
                    var best = g.OrderByDescending(h => h.Score).First().Chunk;
                    return new SimilarProject(
                        g.Key,
                        best.ProjectName,
                        best.OrgUnitCode,
                        Math.Round(g.Sum(h => h.Score), 3),
                        g.Select(h => h.Chunk.DocumentLabel).Distinct().OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList(),
                        Snip(best.Text));
                })
                .OrderByDescending(p => p.Score)
                .Take(maxProjects)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không tìm được dự án tương tự — trả danh sách rỗng.");
            return Array.Empty<SimilarProject>();
        }
    }

    /// <summary>
    /// Xóa chỉ mục cache để tài liệu VỪA DUYỆT xuất hiện trong tri thức ngay (gọi từ bước Approve),
    /// không phải đợi hết TTL.
    /// </summary>
    public virtual void InvalidateIndex() => _cache.Remove(CacheKey);

    private async Task<IReadOnlyList<(KnowledgeChunk Chunk, double Score)>> SearchAsync(
        Guid projectId,
        string? projectName,
        string? projectDescription,
        string? projectOrgUnitCode,
        string query,
        int topK,
        CancellationToken cancellationToken)
    {
        var index = await _cache.GetOrCreateAsync(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return BuildIndexAsync(cancellationToken);
        });
        if (index == null || index.Count == 0)
            return Array.Empty<(KnowledgeChunk, double)>();

        var fullQuery = string.Join(" ",
            new[] { projectName, projectDescription, Truncate(query) }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        return index.Search(
            fullQuery,
            topK,
            filter: chunk => chunk.ProjectId != projectId,
            boost: chunk => projectOrgUnitCode != null
                            && string.Equals(chunk.OrgUnitCode, projectOrgUnitCode, StringComparison.OrdinalIgnoreCase)
                ? SameOrgUnitBoost
                : 1.0);
    }

    // Chỉ mục dựng từ bản MỚI NHẤT của mỗi (dự án, loại tài liệu) đã duyệt — các phiên bản V cũ gần
    // như trùng nội dung, đưa hết vào chỉ làm kết quả lặp đoạn.
    private async Task<Bm25TextIndex> BuildIndexAsync(CancellationToken cancellationToken)
    {
        var documents = await _db.ProjectDocuments.AsNoTracking()
            .Where(d => d.IsApproved && CorpusFileNames.Contains(d.FileName) && d.Content != "")
            .Select(d => new
            {
                d.ProjectId,
                d.FileName,
                d.Content,
                d.CreatedAt,
                ProjectName = d.Project.Name,
                d.Project.OrgUnitCode
            })
            .ToListAsync(cancellationToken);

        var chunks = new List<KnowledgeChunk>();
        foreach (var document in documents
                     .GroupBy(d => new { d.ProjectId, d.FileName })
                     .Select(g => g.OrderByDescending(d => d.CreatedAt).First()))
        {
            var label = DocumentLabels.GetValueOrDefault(document.FileName, document.FileName);
            foreach (var (heading, text) in MarkdownChunker.Split(document.Content))
            {
                chunks.Add(new KnowledgeChunk(
                    document.ProjectId, document.ProjectName, document.OrgUnitCode, label, heading, text));
            }
        }

        return new Bm25TextIndex(chunks);
    }

    private string Render(IReadOnlyList<(KnowledgeChunk Chunk, double Score)> hits)
    {
        var excerpts = new StringBuilder();
        foreach (var (chunk, _) in hits)
        {
            var heading = string.IsNullOrWhiteSpace(chunk.Heading) ? "" : $" — mục \"{chunk.Heading}\"";
            var entry = $"### Dự án \"{chunk.ProjectName}\" — {chunk.DocumentLabel}{heading}\n{chunk.Text}\n\n";
            if (excerpts.Length + entry.Length > MaxTotalChars)
                break;
            excerpts.Append(entry);
        }
        if (excerpts.Length == 0)
            return string.Empty;

        // Khối comment HTML đầu template là ghi chú cho người sửa file — cắt trước khi thay placeholder
        // (cùng cơ chế với OrganizationContextService), model không thấy nó.
        var template = HtmlCommentRegex().Replace(_prompts.Get(TemplatePath), string.Empty);
        return template.Trim().Replace("{{EXCERPTS}}", excerpts.ToString().TrimEnd());
    }

    private static string Truncate(string query) =>
        query.Length > MaxQueryChars ? query[..MaxQueryChars] : query;

    // Snippet một dòng cho panel "Dự án tương tự" — đủ nhận diện nội dung, không tràn card.
    private static string Snip(string text)
    {
        var flattened = text.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= 220 ? flattened : flattened[..220] + "…";
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();
}
