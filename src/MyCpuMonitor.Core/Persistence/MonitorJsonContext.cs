using System.Text.Json.Serialization;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Settings;

namespace MyCpuMonitor.Core.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(LayoutDocument))]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class MonitorJsonContext : JsonSerializerContext;
