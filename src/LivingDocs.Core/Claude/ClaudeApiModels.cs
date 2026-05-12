using System.Text.Json.Serialization;

namespace LivingDocs.Core.Claude;

internal record ClaudeRequest(
    [property: JsonPropertyName("model")]      string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("messages")]   List<ClaudeMessage> Messages
);

internal record ClaudeMessage(
    [property: JsonPropertyName("role")]    string Role,
    [property: JsonPropertyName("content")] string Content
);

internal record ClaudeResponse(
    [property: JsonPropertyName("content")] List<ClaudeContent> Content
);

internal record ClaudeContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text
);
