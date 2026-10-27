namespace DroidDeck.Adb.Tests;

public sealed class AdbEndpointTests
{
    [Fact]
    public void Default_UsesLoopbackAndStandardPort()
    {
        Assert.Equal("127.0.0.1", AdbEndpoint.Default.Host);
        Assert.Equal(5037, AdbEndpoint.Default.Port);
    }

    [Fact]
    public void TryParse_AcceptsHostPortAndTcpPrefix()
    {
        Assert.True(AdbEndpoint.TryParse("10.0.0.5:5038", out var plain));
        Assert.Equal("10.0.0.5", plain.Host);
        Assert.Equal(5038, plain.Port);

        Assert.True(AdbEndpoint.TryParse("tcp:localhost:6000", out var tcp));
        Assert.Equal("localhost", tcp.Host);
        Assert.Equal(6000, tcp.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData(":5037")]
    [InlineData("localhost:0")]
    [InlineData("localhost:70000")]
    [InlineData("localhost:abc")]
    public void TryParse_RejectsInvalidValues(string value) =>
        Assert.False(AdbEndpoint.TryParse(value, out _));

    [Fact]
    public void Resolve_PrefersSocketVariable()
    {
        var endpoint = AdbEndpoint.Resolve(name => name switch
        {
            AdbEndpoint.SocketVariable => "tcp:10.1.2.3:5040",
            _ => null,
        });

        Assert.Equal("10.1.2.3", endpoint.Host);
        Assert.Equal(5040, endpoint.Port);
    }

    [Fact]
    public void Resolve_UsesHostAndPortVariables()
    {
        var endpoint = AdbEndpoint.Resolve(name => name switch
        {
            AdbEndpoint.HostVariable => "192.168.0.9",
            AdbEndpoint.PortVariable => "6001",
            _ => null,
        });

        Assert.Equal("192.168.0.9", endpoint.Host);
        Assert.Equal(6001, endpoint.Port);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99999")]
    [InlineData("not-a-port")]
    public void Resolve_FallsBackWhenPortIsInvalid(string port)
    {
        var endpoint = AdbEndpoint.Resolve(name => name == AdbEndpoint.PortVariable ? port : null);
        Assert.Equal(AdbEndpoint.DefaultPort, endpoint.Port);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Constructor_RejectsOutOfRangePorts(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdbEndpoint("127.0.0.1", port));

    [Fact]
    public void ToString_FormatsHostAndPort() =>
        Assert.Equal("127.0.0.1:5037", AdbEndpoint.Default.ToString());
}
