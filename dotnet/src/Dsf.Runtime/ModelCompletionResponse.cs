using System.Text.Json;

namespace Dsf.Runtime;

internal static class ModelCompletionResponse
{
    public static string ReadText(JsonElement response, string model)
    {
        if (response.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() == 1)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish)
                && finish.ValueKind == JsonValueKind.String && finish.GetString() == "stop"
                && choice.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(content.GetString()))
            {
                return content.GetString()!;
            }
        }

        throw new InvalidOperationException($"{model} returned a malformed or incomplete model completion");
    }
}
