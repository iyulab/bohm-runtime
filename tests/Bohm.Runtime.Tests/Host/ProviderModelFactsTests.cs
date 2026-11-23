using Bohm.Runtime.Host.Llm;
using IronHive.Abstractions.Models;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// What a provider's model information says is read as it says it: a list of features or input modalities it gave is its answer —
/// what is missing from it is not supported — and a list it did not give leaves that fact unknown, to be learned as before.
/// </summary>
public sealed class ProviderModelFactsTests
{
    [Fact]
    public void Thinking_among_the_features_and_image_among_the_inputs_are_said()
    {
        var facts = ProviderModelInfo.FactsOf(new LanguageModelCard
        {
            ModelId = "m",
            Features = ["thinking", "effort", "structured-output"],
            InputModalities = ["text", "image", "pdf"],
        });

        Assert.NotNull(facts);
        Assert.True(facts.Limits.Reasoning);
        Assert.True(facts.Sees);
    }

    [Fact]
    public void Lists_given_without_them_say_the_model_neither_thinks_nor_sees()
    {
        var facts = ProviderModelInfo.FactsOf(new LanguageModelCard { ModelId = "m", Features = [], InputModalities = ["text"] });

        Assert.NotNull(facts);
        Assert.False(facts.Limits.Reasoning);
        Assert.False(facts.Sees);
    }

    [Fact]
    public void Lists_not_given_leave_both_unknown_and_the_limits_stand()
    {
        var facts = ProviderModelInfo.FactsOf(new LanguageModelCard { ModelId = "m", ContextWindow = 200000, MaxOutputTokens = 64000 });

        Assert.NotNull(facts);
        Assert.Null(facts.Limits.Reasoning);
        Assert.Null(facts.Sees);
        Assert.Equal(200000, facts.Limits.ContextWindow);
    }

    [Fact]
    public void A_card_that_says_nothing_is_no_answer()
    {
        Assert.Null(ProviderModelInfo.FactsOf(new LanguageModelCard { ModelId = "m" }));
    }
}
