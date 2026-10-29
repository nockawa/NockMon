using System.Text.Json.Serialization;
using NockMon.Core.Layout;
using NockMon.Core.Settings;

namespace NockMon.Core.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(LayoutDocument))]
[JsonSerializable(typeof(WidgetConfig))]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class MonitorJsonContext : JsonSerializerContext;
