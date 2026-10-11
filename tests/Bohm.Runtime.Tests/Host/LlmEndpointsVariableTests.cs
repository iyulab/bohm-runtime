using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

public sealed class LlmEndpointsVariableTests
{
    [Fact]
    public void Pairs_send_a_known_providers_traffic_to_a_gateway_base_address()
    {
        var (endpoints, unusable) = LlmEndpointsVariable.Parse("api.anthropic.com=https://gateway.example/anthropic ; api.openai.com=http://127.0.0.1:5077/v1/");
        Assert.Empty(unusable);
        Assert.Equal(new Uri("https://gateway.example/anthropic/"), endpoints["api.anthropic.com"]);
        Assert.Equal(new Uri("http://127.0.0.1:5077/v1/"), endpoints["API.OPENAI.COM"]);
    }

    [Theory]
    [InlineData("unknown.example=https://gateway.example/")]   // not a provider Bohm knows
    [InlineData("api.anthropic.com=http://gateway.example/")]   // plain HTTP away from this computer
    [InlineData("api.anthropic.com=not an address")]
    [InlineData("=https://gateway.example/")]
    [InlineData("api.anthropic.com")]
    public void What_cannot_be_used_is_left_out_and_named(string pair)
    {
        var (endpoints, unusable) = LlmEndpointsVariable.Parse(pair);
        Assert.Empty(endpoints);
        Assert.Equal([pair], unusable);
    }

    [Fact]
    public void No_variable_is_no_change()
    {
        var (endpoints, unusable) = LlmEndpointsVariable.Parse(null);
        Assert.Empty(endpoints);
        Assert.Empty(unusable);
    }
}
