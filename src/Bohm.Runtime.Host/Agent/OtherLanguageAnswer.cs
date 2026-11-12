using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Agent;

/// <summary>
/// Notices an answer written in another language than the question: asked in Korean about a page in English, a model now
/// and then answers wholly in English, though it is told to answer in the language of the question. The question's letters
/// are mostly of one script other than Latin (Hangul, kana, Han, Cyrillic …) and the answer's are nearly all Latin. The turn
/// is then asked once more with <see cref="Note"/>, which leaves room for a person who asked for another language.
/// </summary>
internal static class OtherLanguageAnswer
{
    /// <summary>An answer with fewer letters than this is not looked at — a name, a code or a short label is often Latin.</summary>
    internal const int FewestLetters = 40;

    /// <summary>The share of an answer's letters that are Latin at which it is written in a Latin-script language.</summary>
    internal const double LatinShare = 0.9;

    internal const string Note =
        "Your answer above is not in the language the person wrote in. " +
        "Unless they asked for it in another language (a translation, say), write the same answer again in the language they wrote in, " +
        "keeping a name as you wrote it when you are not sure of its usual spelling in that language. " +
        "If they did ask for another language, write it again as before. " +
        "Either way, answer only the person: say nothing about this message or about answering again.";

    /// <summary>Whether <paramref name="answer"/> is in a Latin script while the person's last question in <paramref name="conversation"/> is not.</summary>
    public static bool Differs(string answer, IReadOnlyList<ChatMessage> conversation)
    {
        var question = conversation.LastOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))?.Text;
        if (question is null) return false;
        var (askedLatin, askedOther) = Letters(question);
        if (askedOther == 0 || askedOther < askedLatin) return false;
        var (latin, other) = Letters(answer);
        return latin + other >= FewestLetters && latin >= (latin + other) * LatinShare;
    }

    // Letters of the Latin script, and letters of any other script.
    private static (int Latin, int Other) Letters(string text)
    {
        int latin = 0, other = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            if (c < 'ɐ' || c is >= 'Ḁ' and <= 'ỿ') latin++;
            else other++;
        }
        return (latin, other);
    }
}
