using System.Net;
using System.Net.Sockets;
using Bohm.Runtime.Host;

namespace Bohm.Runtime.Tests.Host;

/// <summary>An application's address includes the port, so a data root keeps its port across launches.</summary>
public sealed class HostAddressTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("bohm-address-").FullName;

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    [Fact]
    public async Task A_data_root_is_served_on_the_same_port_after_a_restart()
    {
        var first = await RunningHost.StartAsync(_dataRoot);
        var port = first.Port;
        await first.StopKeepingDataAsync();

        var second = await RunningHost.StartAsync(_dataRoot);
        try
        {
            Assert.Equal(port, second.Port);
            Assert.Null(second.PreviousPort);
            Assert.Equal(port, HostAddress.Read(_dataRoot));
        }
        finally
        {
            await second.StopKeepingDataAsync();
        }
    }

    [Fact]
    public async Task When_the_remembered_port_is_taken_a_new_one_is_used_remembered_and_reported()
    {
        var first = await RunningHost.StartAsync(_dataRoot);
        var port = first.Port;
        await first.StopKeepingDataAsync();

        using var squatter = new TcpListener(IPAddress.Loopback, port);
        squatter.Start();
        var second = await RunningHost.StartAsync(_dataRoot);
        try
        {
            Assert.NotEqual(port, second.Port);
            Assert.Equal(port, second.PreviousPort);
            Assert.Equal(second.Port, HostAddress.Read(_dataRoot));
        }
        finally
        {
            await second.StopKeepingDataAsync();
        }
    }

    [Fact]
    public async Task A_port_released_moments_later_is_still_reused()
    {
        // The previous runtime of the same data root may still be stopping when the next one starts.
        var first = await RunningHost.StartAsync(_dataRoot);
        var port = first.Port;
        await first.StopKeepingDataAsync();

        var stopping = new TcpListener(IPAddress.Loopback, port);
        stopping.Start();
        var release = Task.Delay(700).ContinueWith(_ => stopping.Stop(), TaskScheduler.Default);
        var second = await RunningHost.StartAsync(_dataRoot);
        try
        {
            await release;
            Assert.Equal(port, second.Port);
            Assert.Null(second.PreviousPort);
        }
        finally
        {
            await second.StopKeepingDataAsync();
            stopping.Dispose();
        }
    }

    [Fact]
    public async Task An_unreadable_record_is_replaced_by_a_new_port()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataRoot, "host.json"), "{ not json");

        var host = await RunningHost.StartAsync(_dataRoot);
        try
        {
            Assert.Null(host.PreviousPort);
            Assert.Equal(host.Port, HostAddress.Read(_dataRoot));
        }
        finally
        {
            await host.StopKeepingDataAsync();
        }
    }

    [Fact]
    public async Task A_port_chosen_by_the_operating_system_each_time_is_not_remembered()
    {
        var host = await RunningHost.StartAsync(_dataRoot, configure: o => o with { Port = 0 });
        try
        {
            Assert.False(File.Exists(Path.Combine(_dataRoot, "host.json")));
        }
        finally
        {
            await host.StopKeepingDataAsync();
        }
    }

    [Fact]
    public void The_record_is_small_readable_json_with_a_format()
    {
        HostAddress.Write(_dataRoot, 51234);

        Assert.Equal("{ \"format\": \"bohm.host/0\", \"port\": 51234 }\n", File.ReadAllText(Path.Combine(_dataRoot, "host.json")));
        Assert.Equal(51234, HostAddress.Read(_dataRoot));
    }
}
