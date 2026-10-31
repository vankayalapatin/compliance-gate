// Tool-calling wire format for the Spark-driven loop. Spark sees tool
// definitions, returns tool calls; C# validates each call against the case
// picker before executing it. Schemas stay flat (scalars plus scalar
// arrays): enough for case ids, document numbers, amounts, and carton lists.
namespace Gate.Contracts;

public enum ModelToolParameterType
{
    String,
    Number,
    Integer,
    Boolean
}

public sealed record ModelToolParameter(
    string Name,
    string Description,
    ModelToolParameterType Type,
    bool Required = true,
    bool IsArray = false);

public sealed record ModelTool(
    string Name,
    string Description,
    IReadOnlyList<ModelToolParameter> Parameters);

public sealed record ModelToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

public sealed record ModelMessage(
    string Role,
    string? Text,
    IReadOnlyList<ModelToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null);
