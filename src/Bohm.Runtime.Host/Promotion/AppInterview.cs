using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Promotion;

/// <summary>
/// Questions before an application is made from what was asked. When the request leaves open something that would change
/// what gets built, the model that writes applications asks a few short questions, each with likely answers the person can
/// pick (or answer in their own words); when the request is clear enough it asks nothing. Whether to ask is the model's call —
/// the host only bounds the shape: at most <see cref="MaxQuestions"/> questions of at most <see cref="MaxOptions"/> answers.
/// </summary>
internal static class AppInterview
{
    public const int MaxQuestions = 4;
    public const int MaxOptions = 4;
    private const int MaxQuestionLength = 200;
    private const int MaxOptionLength = 80;

    /// <summary>Room for a short list — and for the thinking a model that thinks does first.</summary>
    private const int MaxOutputTokens = 3000;

    private const string Instructions = """
        A person asked for a small web application they will keep using. Before it is written, decide whether the request
        leaves open something that would change what you build: what it is for, what it keeps or tracks, who uses it, what
        it shows first. If the request already settles those, ask nothing. Otherwise ask at most four short questions, one
        thing each, in the person's language, each with two to four short answers that cover what they most likely mean
        (they can also answer in their own words). Do not ask about colors, names or style unless the request turns on them,
        and do not ask what you can sensibly decide yourself.

        Answer with JSON only, no other text:
        {"questions":[{"question":"…","options":["…","…"]}]}
        with an empty list when nothing needs asking.
        """;

    /// <summary>
    /// Asks <paramref name="model"/> what to ask about <paramref name="request"/>. An answer that cannot be read as the list
    /// asked for is no questions — the application is made from the request as it stands.
    /// </summary>
    public static async Task<(IReadOnlyList<InterviewQuestion> Questions, Llm.ModelUse? Use)> AskAsync(IChatClient model, string request, string? lang,
        CancellationToken cancellationToken)
    {
        var language = string.IsNullOrWhiteSpace(lang) ? "" : $" (the person's language: {lang.Trim()})";
        var response = await model.GetResponseAsync(
            [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, $"The request{language}: «{request.Trim()}»")],
            new ChatOptions { MaxOutputTokens = MaxOutputTokens, Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } },
            cancellationToken).ConfigureAwait(false);
        var usage = response.Usage is { } u && ((u.InputTokenCount ?? 0) > 0 || (u.OutputTokenCount ?? 0) > 0)
            ? new Llm.ModelUse(u.InputTokenCount ?? 0, u.OutputTokenCount ?? 0, u.CachedInputTokenCount ?? 0)
            : null;
        return (Read(response.Text), usage);
    }

    /// <summary>
    /// The questions in a model's answer: the first JSON object in it, each question with its answers cut to the bounds;
    /// a question with fewer than two answers is left out.
    /// </summary>
    internal static IReadOnlyList<InterviewQuestion> Read(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return [];
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            return [];
        try
        {
            using var document = JsonDocument.Parse(answer[start..(end + 1)]);
            if (!document.RootElement.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
                return [];
            var read = new List<InterviewQuestion>();
            foreach (var item in questions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("question", out var question) || question.ValueKind != JsonValueKind.String
                    || question.GetString()?.Trim() is not { Length: > 0 } text
                    || !item.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
                    continue;
                var answers = options.EnumerateArray()
                    .Where(o => o.ValueKind == JsonValueKind.String)
                    .Select(o => Cut(o.GetString()!.Trim(), MaxOptionLength))
                    .Where(o => o.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaxOptions)
                    .ToList();
                if (answers.Count >= 2)
                    read.Add(new InterviewQuestion(Cut(text, MaxQuestionLength), answers));
                if (read.Count == MaxQuestions)
                    break;
            }

            return read;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Cut(string text, int length) => text.Length > length ? text[..length] : text;
}

/// <summary>One question before making an application: what is asked and the answers offered.</summary>
internal sealed record InterviewQuestion(string Question, IReadOnlyList<string> Options);
