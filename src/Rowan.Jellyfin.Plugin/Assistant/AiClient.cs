using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rowan.Jellyfin.Plugin.Assistant;

/// <summary>Stateless OpenAI Chat Completions wire protocol; no hosted conversations or provider-side tools.</summary>
public sealed class AiClient(HttpClient http)
{
    public async Task<JsonElement> Complete(AiOptions options, IReadOnlyList<object> messages, object[] tools, CancellationToken token)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = options.Model, ["messages"] = messages, ["tools"] = tools,
            ["stream"] = false, ["store"] = false,
            [options.LegacyTokenLimit ? "max_tokens" : "max_completion_tokens"] = 2048
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, options.CompletionsUri);
        if (!string.IsNullOrEmpty(options.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = new StringContent(AiJson.Write(body), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AiException("AI endpoint rejected the call. Check the saved endpoint, model, key and token-limit setting.");
            if (response.Content.Headers.ContentLength > 1_048_576) throw new AiException("AI response exceeded the safety limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > 1_048_576) throw new AiException("AI response exceeded the safety limit.");
                buffer.Write(bytes, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var choices = document.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() != 1) throw new JsonException();
            var choice = choices[0];
            var finish = choice.GetProperty("finish_reason").GetString();
            if (finish is not ("stop" or "tool_calls")) throw new AiException("The model did not finish its response. Try a shorter question or a model with a larger output budget.");
            var message = choice.GetProperty("message");
            if (message.ValueKind != JsonValueKind.Object) throw new JsonException();
            return message.Clone();
        }
        catch (HttpRequestException) { throw new AiException("Cannot reach the configured AI endpoint."); }
        catch (IOException) { throw new AiException("The AI connection was interrupted."); }
        catch (JsonException) { throw new AiException("The endpoint returned an invalid Chat Completions response."); }
        catch (InvalidOperationException) { throw new AiException("The endpoint returned an invalid Chat Completions response."); }
        catch (KeyNotFoundException) { throw new AiException("The endpoint returned an invalid Chat Completions response."); }
    }
}
