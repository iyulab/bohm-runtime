using Bohm.Runtime.Host.Edit;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Host.Promotion;

/// <summary>
/// Which AI answers an application's calls on this computer, said to the model that writes one — so an
/// application made here calls the AI that is connected here, not whichever provider the model happened
/// to pick (a call to a provider with no key is refused with no_key). The same order the relay answers in
/// (<see cref="LlmProxy"/>): a request without a key goes to the organization's model server or the model
/// on this computer, whatever its shape; a provider whose key is connected answers its own requests.
/// </summary>
internal static class AppAi
{
    /// <summary>One line for the model, or <see langword="null"/> when no AI would answer an application's call.</summary>
    /// <param name="hasKey">Whether a provider's key is connected.</param>
    /// <param name="keyless">Whether the organization's model server or a model on this computer answers requests without a key.</param>
    /// <param name="chosen">The provider and model chosen for writing and changing applications, if one is.</param>
    /// <param name="keylessTranscribes">Whether the organization's model server, answering without a key, has a model that turns speech into text (<see cref="CompanyModel.TranscriptionModelAsync"/>).</param>
    public static string? Line(Func<LlmProvider, bool> hasKey, bool keyless, EditModelChoice? chosen, bool keylessTranscribes = false)
    {
        var openai = LlmProviders.ById("openai")!;
        // Speech to text answers in the OpenAI shape only: with OpenAI's key, or bridged to the organization's server.
        var speech = hasKey(openai)
            ? " To turn a recording into text, POST it as multipart form data (fields file and model) to "
                + "https://api.openai.com/v1/audio/transcriptions with the model \"whisper-1\"; the answer's text field is what was said."
            : keyless && keylessTranscribes
                ? " To turn a recording into text, POST it as multipart form data (fields file and model) to "
                    + "https://api.openai.com/v1/audio/transcriptions with any model name; the answer's text field is what was said."
                : " Nothing here turns speech into text: keep a recording if it helps and let the person write what was said.";
        if (keyless && !hasKey(openai))
            return "When the application calls an AI, call the OpenAI Chat Completions API (https://api.openai.com/v1/chat/completions) with "
                + "any model name: the AI connected on this computer answers it. Other providers' APIs have no key here." + speech;

        var provider = chosen is { } c && LlmProviders.ById(c.Provider) is { } p && hasKey(p) ? p : LlmProviders.All.FirstOrDefault(hasKey);
        if (provider is null) return null;
        var model = chosen?.Provider == provider.Id ? chosen.Model : null;
        var call = provider.Id switch
        {
            "anthropic" => "the Anthropic Messages API (https://api.anthropic.com/v1/messages)",
            "google" => $"the Gemini API (https://generativelanguage.googleapis.com/v1beta/models/{model ?? "<model>"}:generateContent)",
            _ => $"the {provider.DisplayName} chat completions API (https://{provider.Host}/{provider.OpenAICompatiblePath}chat/completions)",
        };
        return $"When the application calls an AI, call {call}" + (model is null ? " with a current model" : $" with the model \"{model}\"")
            + $": {provider.DisplayName} is the AI connected on this computer. Other providers' APIs have no key here." + speech;
    }
}
