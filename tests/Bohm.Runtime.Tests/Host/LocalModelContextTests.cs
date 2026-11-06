using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The context a model on this computer is started with when the person has not named one: the length the runtime's own
/// requests are written for, within what the model was trained on — named to the library, so it is not bounded by its memory estimate.
/// </summary>
public sealed class LocalModelContextTests
{
    [Fact]
    public void A_model_trained_on_more_starts_with_the_working_length()
    {
        Assert.Equal(LocalModel.WorkingContextLength, LocalModel.Working(trained: 262144));
    }

    [Fact]
    public void The_length_never_passes_the_trained_length()
    {
        Assert.Equal(8192, LocalModel.Working(trained: 8192));
    }

    [Fact]
    public void A_file_that_does_not_say_its_trained_length_gets_the_working_length()
    {
        Assert.Equal(LocalModel.WorkingContextLength, LocalModel.Working(trained: null));
        Assert.Equal(LocalModel.WorkingContextLength, LocalModel.Working(trained: 0));
    }
}
