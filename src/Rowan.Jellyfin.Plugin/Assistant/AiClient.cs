using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rowan.Jellyfin.Plugin.Assistant;

/// <summary>Stateless OpenAI-compatible transport; no hosted conversations or provider-side tools.</summary>
public sealed class AiClient(HttpClient http)
{
    // Ephemeral, within-turn replay only. Never saved in chat history or returned to a browser.
    public sealed record ResponseReplay(JsonElement Output);

    public async Task<JsonElement> Complete(AiOptions options, IReadOnlyList<object> messages, object[] tools, CancellationToken token)
    {
        var body = options.UseResponses ? ResponsesBody(options, messages, tools) : new Dictionary<string, object>
        {
            ["model"] = options.Model, ["messages"] = messages, ["tools"] = tools,
            ["stream"] = false, ["store"] = false,
            [options.LegacyTokenLimit ? "max_tokens" : "max_completion_tokens"] = 2048
        };
        if (!options.UseResponses && options.ReasoningEffort is { } effort) body["reasoning_effort"] = effort;
        using var request = new HttpRequestMessage(HttpMethod.Post, options.EndpointUri);
        if (!string.IsNullOrEmpty(options.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = new StringContent(AiJson.Write(body), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AiException("AI endpoint rejected the call. Check the saved endpoint, model, key, API mode, reasoning effort and token-limit setting.");
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
            if (options.UseResponses) return ReadResponse(document.RootElement);
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
        catch (JsonException) { throw new AiException("The endpoint returned an invalid AI response for the selected API mode."); }
        catch (InvalidOperationException) { throw new AiException("The endpoint returned an invalid AI response for the selected API mode."); }
        catch (KeyNotFoundException) { throw new AiException("The endpoint returned an invalid AI response for the selected API mode."); }
    }

    private static Dictionary<string, object> ResponsesBody(AiOptions options, IReadOnlyList<object> messages, object[] tools)
    {
        var input = new List<object>();
        foreach (var item in messages)
        {
            if (item is ResponseReplay replay) { input.AddRange(replay.Output.EnumerateArray().Select(v => (object)v.Clone())); continue; }
            var message = JsonSerializer.SerializeToElement(item, AiJson.Options);
            if (message.GetProperty("role").GetString() == "tool") input.Add(new
            {
                type = "function_call_output", call_id = message.GetProperty("tool_call_id").GetString(), output = message.GetProperty("content").GetString()
            });
            else input.Add(message);
        }
        var functions = tools.Select(tool =>
        {
            var function = JsonSerializer.SerializeToElement(tool, AiJson.Options).GetProperty("function");
            return new { type = "function", name = function.GetProperty("name").GetString(), description = function.GetProperty("description").GetString(), parameters = function.GetProperty("parameters").Clone(), strict = false };
        }).ToArray();
        var body = new Dictionary<string, object>
        {
            ["model"] = options.Model, ["input"] = input, ["tools"] = functions, ["store"] = false, ["stream"] = false,
            // Reasoning and visible output share this cap; 2K can be exhausted before a tool call.
            ["max_output_tokens"] = 8192, ["include"] = new[] { "reasoning.encrypted_content" }
        };
        if (options.ReasoningEffort is { } effort) body["reasoning"] = new { effort };
        return body;
    }

    private static JsonElement ReadResponse(JsonElement response)
    {
        if (response.GetProperty("status").GetString() != "completed") throw new AiException("The model did not finish its response. Try lower reasoning effort or a shorter question.");
        var output = response.GetProperty("output");
        var text = new List<string>(); var calls = new List<object>();
        foreach (var item in output.EnumerateArray())
        {
            switch (item.GetProperty("type").GetString())
            {
                case "reasoning": break; // Replayed encrypted, never displayed or persisted.
                case "function_call":
                    calls.Add(new { id = item.GetProperty("call_id").GetString(), type = "function", function = new
                    { name = item.GetProperty("name").GetString(), arguments = item.GetProperty("arguments").GetString() } });
                    break;
                case "message":
                    if (item.GetProperty("role").GetString() != "assistant") throw new JsonException();
                    foreach (var part in item.GetProperty("content").EnumerateArray())
                    {
                        var type = part.GetProperty("type").GetString();
                        if (type == "output_text") text.Add(part.GetProperty("text").GetString() ?? "");
                        else if (type == "refusal") text.Add(part.GetProperty("refusal").GetString() ?? "");
                        else throw new JsonException();
                    }
                    break;
                default: throw new AiException("The endpoint returned an unsupported response item. Only configured function tools are allowed.");
            }
        }
        return JsonSerializer.SerializeToElement(new { content = string.Join("\n\n", text), tool_calls = calls, response_output = output.Clone() }, AiJson.Options);
    }
}
