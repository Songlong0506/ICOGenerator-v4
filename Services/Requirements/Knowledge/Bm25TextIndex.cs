namespace ICOGenerator.Services.Requirements.Knowledge;

/// <summary>
/// Chỉ mục truy xuất lexical (BM25) trên các <see cref="KnowledgeChunk"/> — thuần in-memory, không cần
/// hạ tầng embedding/vector: corpus là tài liệu đã duyệt (cỡ trăm–nghìn đoạn) nên dựng chỉ mục chỉ tốn
/// vài ms và provider-agnostic (SqlServer lẫn Sqlite). Tiếng Việt viết rời từng âm tiết nên tokenizer
/// chỉ cần tách theo ký tự chữ/số Unicode; IDF của BM25 tự dìm các âm tiết quá phổ biến, kèm một danh
/// sách stopword nhỏ cho các từ chức năng dày đặc nhất.
/// </summary>
public sealed class Bm25TextIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        // Từ chức năng tiếng Việt phổ biến nhất trong văn bản yêu cầu — không mang nghĩa phân biệt.
        "và", "của", "cho", "các", "là", "một", "có", "được", "trong", "với", "này", "đó", "khi",
        "để", "không", "phải", "sẽ", "cần", "từ", "theo", "trên", "hoặc", "những", "cũng", "như",
        "đã", "bị", "về", "ra", "vào", "thì", "mà", "tại", "sau", "trước", "nếu", "đến", "bằng",
        // Vài từ tiếng Anh hay lẫn trong tài liệu song ngữ.
        "the", "and", "for", "with", "that", "this", "are", "was", "will", "not"
    };

    private readonly List<KnowledgeChunk> _chunks;
    private readonly List<Dictionary<string, int>> _termFrequencies;
    private readonly Dictionary<string, int> _documentFrequencies;
    private readonly double _averageLength;

    public Bm25TextIndex(IReadOnlyList<KnowledgeChunk> chunks)
    {
        _chunks = chunks.ToList();
        _termFrequencies = new List<Dictionary<string, int>>(_chunks.Count);
        _documentFrequencies = new Dictionary<string, int>(StringComparer.Ordinal);

        long totalLength = 0;
        foreach (var chunk in _chunks)
        {
            // Heading tính vào nội dung đoạn: tên mục ("Luồng duyệt", "Phạm vi") thường chính là từ khóa truy vấn.
            var tokens = Tokenize(chunk.Heading == null ? chunk.Text : chunk.Heading + " " + chunk.Text);
            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in tokens)
                frequencies[token] = frequencies.GetValueOrDefault(token) + 1;

            _termFrequencies.Add(frequencies);
            totalLength += tokens.Count;
            foreach (var term in frequencies.Keys)
                _documentFrequencies[term] = _documentFrequencies.GetValueOrDefault(term) + 1;
        }

        _averageLength = _chunks.Count == 0 ? 0 : (double)totalLength / _chunks.Count;
    }

    public int Count => _chunks.Count;

    /// <summary>
    /// Các đoạn khớp truy vấn tốt nhất (điểm BM25 &gt; 0), sắp giảm dần theo điểm sau khi nhân
    /// <paramref name="boost"/>. <paramref name="filter"/> loại đoạn ngay trước khi chấm (vd đoạn
    /// của chính dự án đang hỏi).
    /// </summary>
    public IReadOnlyList<(KnowledgeChunk Chunk, double Score)> Search(
        string query,
        int topK,
        Func<KnowledgeChunk, bool>? filter = null,
        Func<KnowledgeChunk, double>? boost = null)
    {
        var queryTerms = Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        if (queryTerms.Count == 0 || _chunks.Count == 0)
            return Array.Empty<(KnowledgeChunk, double)>();

        var scored = new List<(KnowledgeChunk Chunk, double Score)>();
        for (var i = 0; i < _chunks.Count; i++)
        {
            var chunk = _chunks[i];
            if (filter != null && !filter(chunk))
                continue;

            var frequencies = _termFrequencies[i];
            var length = frequencies.Values.Sum();
            double score = 0;
            foreach (var term in queryTerms)
            {
                if (!frequencies.TryGetValue(term, out var tf))
                    continue;
                var df = _documentFrequencies[term];
                var idf = Math.Log(1 + (_chunks.Count - df + 0.5) / (df + 0.5));
                score += idf * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * length / Math.Max(_averageLength, 1)));
            }

            if (score <= 0)
                continue;
            if (boost != null)
                score *= boost(chunk);
            scored.Add((chunk, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>Token chữ/số Unicode, lowercase, dài ≥ 2 và không thuộc stopword.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWordChar = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isWordChar)
            {
                if (start < 0)
                    start = i;
                continue;
            }
            if (start >= 0)
            {
                var token = text[start..i].ToLowerInvariant();
                if (token.Length >= 2 && !Stopwords.Contains(token))
                    tokens.Add(token);
                start = -1;
            }
        }

        return tokens;
    }
}
