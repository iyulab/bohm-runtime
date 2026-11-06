using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The context a model on this computer is started with when the person has not named one: what the library chose,
/// unless that leaves the runtime's own requests no room — then the length they are written for, within what the model was trained on.
/// </summary>
public sealed class LocalModelContextTests
{
    [Fact]
    public void A_context_shrunk_below_the_working_length_is_raised_to_it()
    {
        Assert.Equal(LocalModel.WorkingContextLength, LocalModel.Reload(started: 4096, trained: 262144));
    }

    [Fact]
    public void A_context_the_library_chose_at_or_above_the_working_length_is_kept()
    {
        Assert.Null(LocalModel.Reload(started: LocalModel.WorkingContextLength, trained: 262144));
        Assert.Null(LocalModel.Reload(started: 65536, trained: 262144));
    }

    [Fact]
    public void The_raise_never_passes_the_trained_length()
    {
        Assert.Equal(8192, LocalModel.Reload(started: 4096, trained: 8192));
        Assert.Null(LocalModel.Reload(started: 8192, trained: 8192));
    }

    [Fact]
    public void A_file_that_does_not_say_its_trained_length_gets_the_working_length()
    {
        Assert.Equal(LocalModel.WorkingContextLength, LocalModel.Reload(started: 4096, trained: null));
    }
}
