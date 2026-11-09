using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Discovery;

namespace Rowan.Jellyfin.Plugin.Assistant;

public static class AiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write(object value) => JsonSerializer.Serialize(value, Options);
    public static string Clip(string? value, int length) => (value ?? string.Empty)[..Math.Min(value?.Length ?? 0, length)];
}

public sealed record AiOptions(Uri EndpointUri, [property: System.Text.Json.Serialization.JsonIgnore] string? ApiKey, string Model, int DailyLimit,
    bool LegacyTokenLimit = false, bool UseResponses = false, string? ReasoningEffort = "low")
{
    public override string ToString() => "AiOptions { credentials = [REDACTED] }";
    public static AiOptions? From(PluginConfiguration? config)
    {
        if (config?.AiEnabled != true || !Uri.TryCreate(config.AiBaseUrl?.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrWhiteSpace(config.AiModel) || config.AiModel.Length > 200 || config.AiModel.Any(char.IsControl) ||
            (config.AiApiKey?.Any(char.IsControl) ?? false)) return null;
        var mode = config.AiApiMode ?? "auto";
        var effort = config.AiReasoningEffort ?? "low";
        if (mode is not ("auto" or "chat" or "responses") || effort is not ("default" or "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max")) return null;
        var model = config.AiModel.Trim();
        var gpt6 = model.Equals("gpt-6", StringComparison.OrdinalIgnoreCase) || model.StartsWith("gpt-6-", StringComparison.OrdinalIgnoreCase) || model.StartsWith("gpt-6.", StringComparison.OrdinalIgnoreCase);
        var responses = mode == "responses" || mode == "auto" && gpt6;
        // The administrator deliberately chooses a trusted endpoint. LAN HTTP supports local Ollama.
        return new(new Uri(uri, responses ? "responses" : "chat/completions"), config.AiApiKey?.Trim(), model,
            Math.Clamp(config.AiDailyTurnLimit, 1, 1000), config.AiLegacyTokenLimit, responses, effort == "default" ? null : effort);
    }
}

public sealed record AiCard(string Key, string Title, string MediaType, int? TmdbId = null,
    Guid? LibraryItemId = null, string? Date = null, string? PosterPath = null, string? Overview = null,
    int? RuntimeMinutes = null, string[]? Genres = null, bool? Watched = null, double? CommunityRating = null);

public sealed class AiProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required AiCard Card { get; set; }
    public required CreateSeerrRequest Selection { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(10);
    public string State { get; set; } = "pending";
    public string? Result { get; set; }
}

public sealed class AiMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = "assistant";
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public List<AiCard> Cards { get; set; } = [];
    public List<AiProposal> Proposals { get; set; } = [];
}

public sealed class AiThread
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New conversation";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AiMessage> Messages { get; set; } = [];
}

public sealed record AiMemory(Guid Id, string Text);
public sealed class AiUserData
{
    public List<AiThread> Threads { get; set; } = [];
    public List<AiMemory> Memory { get; set; } = [];
    public bool Personalization { get; set; } = true;
    public bool LearnMemory { get; set; } = true;
    public DateOnly UsageDay { get; set; }
    public int Turns { get; set; }
}

public sealed class AiException(string message, int status = 502) : Exception(message)
{
    public int Status { get; } = status;
}
