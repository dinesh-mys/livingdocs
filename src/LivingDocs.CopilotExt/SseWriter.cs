using System.Text;
using System.Text.Json;

public static class SseWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task WriteTextAsync(Stream output, string text)
    {
        var id = Guid.NewGuid().ToString("N")[..8];

        await WriteChunkAsync(output, id, role: "assistant", content: null);

        // Stream in 30-char chunks for a typing effect
        for (int i = 0; i < text.Length; i += 30)
        {
            var chunk = text.Substring(i, Math.Min(30, text.Length - i));
            await WriteChunkAsync(output, id, role: null, content: chunk);
        }

        await WriteDoneAsync(output);
    }

    private static async Task WriteChunkAsync(
        Stream output, string id, string? role, string? content)
    {
        var delta = new Dictionary<string, object?>();
        if (role    is not null) delta["role"]    = role;
        if (content is not null) delta["content"] = content;

        var payload = new
        {
            id      = $"chatcmpl-{id}",
            @object = "chat.completion.chunk",
            choices = new[] { new { index = 0, delta, finish_reason = (string?)null } }
        };

        var line = $"data: {JsonSerializer.Serialize(payload, JsonOpts)}\n\n";
        await output.WriteAsync(Encoding.UTF8.GetBytes(line));
        await output.FlushAsync();
    }

    private static async Task WriteDoneAsync(Stream output)
    {
        await output.WriteAsync("data: [DONE]\n\n"u8.ToArray());
        await output.FlushAsync();
    }
}
