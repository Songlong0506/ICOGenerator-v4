using System.Globalization;
using System.Threading.Channels;
using ICOGenerator.Configuration;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace ICOGenerator.Services.Logging;

/// <summary>
/// Sink Serilog chép các sự kiện từ <see cref="DatabaseLogSettings.MinimumLevel"/> trở lên vào hàng đợi
/// trong bộ nhớ; <see cref="DatabaseLogWriter"/> rút hàng đợi và ghi xuống bảng <c>SystemLogs</c> theo lô.
/// Được Serilog nhặt qua <c>ReadFrom.Services</c> trong <c>Program.cs</c> vì đăng ký DI dưới dạng
/// <see cref="ILogEventSink"/> — không cần khai trong section <c>Serilog:WriteTo</c>.
///
/// Hai rào chắn có chủ đích:
/// <list type="bullet">
///   <item><b>Không ghi DB ngay trong <see cref="Emit"/></b>: Emit chạy đồng bộ trên luồng đang ghi log
///         (request, worker); một lệnh INSERT ở đây làm mọi lời gọi log chậm theo DB, và DB treo thì app treo.</item>
///   <item><b>Chặn vòng lặp</b>: chính lúc ghi lô, EF Core cũng ghi log (cảnh báo truy vấn, lỗi kết nối).
///         Những sự kiện đó sinh ra trong <see cref="Suppress"/> nên bị bỏ qua — chúng vẫn ra file/console,
///         chỉ không quay lại hàng đợi DB để tạo thêm lần ghi khác.</item>
/// </list>
/// Hàng đợi có trần <see cref="Capacity"/>; DB không ghi được lâu thì bỏ dòng CŨ nhất để giữ lỗi mới nhất.
/// </summary>
public sealed class DatabaseLogSink : ILogEventSink
{
    public const int Capacity = 10_000;

    // Giới hạn độ dài để một exception khổng lồ (vd chuỗi JSON response bị dump vào message) không thành
    // một dòng vài MB trong DB. File log vẫn giữ bản đầy đủ.
    internal const int MaxMessageLength = 8_000;
    internal const int MaxExceptionLength = 32_000;
    internal const int MaxPropertiesLength = 8_000;
    internal const int MaxSourceContextLength = 300;
    internal const int MaxRequestPathLength = 500;

    private static readonly AsyncLocal<bool> Suppressed = new();
    private static readonly JsonValueFormatter PropertyFormatter = new(typeTagName: null);

    private readonly SystemLogLevel _minimumLevel;
    private readonly Channel<SystemLog> _queue = Channel.CreateBounded<SystemLog>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public DatabaseLogSink(IOptions<DatabaseLogSettings> settings)
        => _minimumLevel = settings.Value.MinimumLevel;

    /// <summary>Đầu đọc của hàng đợi — chỉ <see cref="DatabaseLogWriter"/> dùng.</summary>
    public ChannelReader<SystemLog> Reader => _queue.Reader;

    /// <summary>
    /// Mở một vùng mà mọi sự kiện log sinh ra bên trong (kể cả qua các lời gọi async) KHÔNG vào hàng đợi DB.
    /// Dùng quanh thao tác ghi/xoá bảng <c>SystemLogs</c>.
    /// </summary>
    public static IDisposable Suppress()
    {
        var previous = Suppressed.Value;
        Suppressed.Value = true;
        return new SuppressScope(previous);
    }

    public void Emit(LogEvent logEvent)
    {
        if (Suppressed.Value)
            return;

        var level = ToSystemLogLevel(logEvent.Level);
        if (level < _minimumLevel)
            return;

        _queue.Writer.TryWrite(ToEntity(logEvent, level));
    }

    internal static SystemLog ToEntity(LogEvent logEvent, SystemLogLevel level)
    {
        string? sourceContext = null;
        string? requestPath = null;
        using var properties = new StringWriter(CultureInfo.InvariantCulture);
        var wroteAny = false;

        foreach (var (name, value) in logEvent.Properties)
        {
            if (name == "SourceContext")
            {
                sourceContext = ScalarText(value);
                continue;
            }
            if (name == "RequestPath")
                requestPath = ScalarText(value);

            properties.Write(wroteAny ? ',' : '{');
            JsonValueFormatter.WriteQuotedJsonString(name, properties);
            properties.Write(':');
            PropertyFormatter.Format(value, properties);
            wroteAny = true;
        }
        if (wroteAny)
            properties.Write('}');

        return new SystemLog
        {
            Timestamp = logEvent.Timestamp.UtcDateTime,
            Level = level,
            SourceContext = Clip(sourceContext, MaxSourceContextLength),
            Message = Clip(logEvent.RenderMessage(CultureInfo.InvariantCulture), MaxMessageLength) ?? string.Empty,
            Exception = Clip(logEvent.Exception?.ToString(), MaxExceptionLength),
            RequestPath = Clip(requestPath, MaxRequestPathLength),
            // JSON bị cắt giữa chừng thì không còn là JSON — quá dài thì bỏ hẳn, giữ đánh dấu cho người đọc.
            Properties = !wroteAny ? null
                : properties.GetStringBuilder().Length <= MaxPropertiesLength ? properties.ToString()
                : "{\"_truncated\":true}"
        };
    }

    internal static SystemLogLevel ToSystemLogLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => SystemLogLevel.Verbose,
        LogEventLevel.Debug => SystemLogLevel.Debug,
        LogEventLevel.Information => SystemLogLevel.Information,
        LogEventLevel.Warning => SystemLogLevel.Warning,
        LogEventLevel.Error => SystemLogLevel.Error,
        _ => SystemLogLevel.Fatal
    };

    private static string? ScalarText(LogEventPropertyValue value)
        => value is ScalarValue { Value: var v } ? v?.ToString() : value.ToString();

    private static string? Clip(string? text, int max)
        => text is null || text.Length <= max ? text : text[..max];

    private sealed class SuppressScope : IDisposable
    {
        private readonly bool _previous;
        public SuppressScope(bool previous) => _previous = previous;
        public void Dispose() => Suppressed.Value = _previous;
    }
}
