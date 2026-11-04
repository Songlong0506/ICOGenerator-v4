namespace ICOGenerator.Services.Workflows;

/// <summary>
/// Cấu hình cách dựng bản POC (section <c>"PocGeneration"</c> trong appsettings.json):
///
///   "PocGeneration": { "OneShot": true, "MaxRepairRounds": 2 }
///
/// <list type="bullet">
///   <item><see cref="OneShot"/>: dựng bằng <see cref="PocOneShotBuilder"/> (một lượt sinh + vài lượt sửa
///         độc lập) thay vì agent loop nhiều bước. Tắt ⇒ mọi lượt dựng đi đường agent như trước.</item>
///   <item><see cref="MaxRepairRounds"/>: số lượt sửa tối đa sau lượt sinh, mỗi lượt sau một lần audit.
///         0 ⇒ chỉ sinh + audit một lần, còn issue thì để người duyệt quyết ở cổng.</item>
/// </list>
/// </summary>
public sealed class PocGenerationSettings
{
    public const string SectionName = "PocGeneration";

    /// <summary>Trần cứng của <see cref="MaxRepairRounds"/> — cấu hình lớn hơn bị kẹp lại.</summary>
    public const int RepairRoundsCeiling = 5;

    public const int DefaultMaxRepairRounds = 2;

    public bool OneShot { get; }

    public int MaxRepairRounds { get; }

    public PocGenerationSettings(IConfiguration configuration)
        : this(
            configuration.GetValue($"{SectionName}:OneShot", true),
            configuration.GetValue($"{SectionName}:MaxRepairRounds", DefaultMaxRepairRounds))
    {
    }

    public PocGenerationSettings(bool oneShot = true, int maxRepairRounds = DefaultMaxRepairRounds)
    {
        OneShot = oneShot;
        MaxRepairRounds = Math.Clamp(maxRepairRounds, 0, RepairRoundsCeiling);
    }
}
