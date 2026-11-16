using System.ComponentModel;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// Câu trả lời của model TRỌNG TÀI PHÂN XỬ trong kiểm toán trọng tài (prompt
/// <c>Eval/judge-audit-arbiter.v1.md</c>): phán quyết ĐÚNG theo đúng schema của lượt trọng tài đang kiểm toán,
/// kèm lời giải thích cho người đọc lại. Generic theo kiểu phán quyết để mức <c>json_schema</c> sinh đúng
/// schema của từng loại trọng tài.
/// </summary>
public sealed class JudgeAuditArbiterReply<TVerdict> where TVerdict : class
{
    [Description("Phán quyết ĐÚNG cho đầu vào này, theo đúng luật và đúng định dạng JSON mà luật của trọng tài quy định.")]
    public TVerdict? Verdict { get; set; }

    [Description("Tối đa 3 câu: phán quyết nào sai ở trường nào, vì sao. Tất cả đều đúng thì ghi ngắn 'đều đúng'.")]
    public string? Notes { get; set; }
}
