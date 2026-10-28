using DroidDeck.Adb.Wireless;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Wireless;

namespace DroidDeck.Adb.Tests.Wireless;

public sealed class WirelessAdbServiceTests
{
    [Fact]
    public async Task DiscoverAsync_ParsesPairingAndConnectServices()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => request.Arguments.SequenceEqual(["mdns", "services"])
                ? FakeProcessRunner.Success(
                    request,
                    "List of discovered mdns services\n" +
                    "adb-ABC123-xyz _adb-tls-pairing._tcp 192.168.1.20:37123\n" +
                    "adb-ABC123-xyz _adb-tls-connect._tcp 192.168.1.20:41871\n")
                : FakeProcessRunner.Success(request),
        };
        await using var service = CreateService(runner);

        var services = await service.DiscoverAsync();

        Assert.Equal(2, services.Count);
        var pairing = Assert.Single(services, static service => service.Kind == WirelessServiceKind.Pairing);
        Assert.Equal("adb-ABC123-xyz", pairing.Name);
        Assert.Equal("192.168.1.20:37123", pairing.Address);
        Assert.Equal("Ready to pair", pairing.StateLabel);
        var connect = Assert.Single(services, static service => service.Kind == WirelessServiceKind.Connect);
        Assert.Equal("192.168.1.20:41871", connect.Address);
        Assert.Equal("Available", connect.StateLabel);
    }

    [Fact]
    public void CreatePairingSession_BuildsQrPayloadAndPng()
    {
        var runner = new FakeProcessRunner();
        var service = CreateService(runner);

        var session = service.CreatePairingSession();

        Assert.StartsWith("droiddeck-", session.ServiceName, StringComparison.Ordinal);
        Assert.Equal(6, session.PairingCode.Length);
        Assert.All(session.PairingCode, static character => Assert.True(char.IsAsciiDigit(character)));
        Assert.Equal(
            $"WIFI:T:ADB;S:{session.ServiceName};P:{session.PairingCode};;",
            session.QrPayload);
        Assert.NotNull(session.QrPng);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, session.QrPng!.Take(4));
    }

    [Fact]
    public async Task CompleteQrPairingAsync_PairsWithScannedDeviceAndConnects()
    {
        var session = CreateSession("droiddeck-scanme", "123456");
        var paired = false;
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                if (request.Arguments.SequenceEqual(["mdns", "services"]))
                {
                    return FakeProcessRunner.Success(
                        request,
                        paired
                            ? "List of discovered mdns services\n" +
                              "adb-ABC123-xyz _adb-tls-connect._tcp 192.168.1.20:41871\n"
                            : $"List of discovered mdns services\n{session.ServiceName} _adb-tls-pairing._tcp 192.168.1.20:37123\n");
                }

                if (request.Arguments.Count >= 2 && request.Arguments[0] == "pair")
                {
                    paired = true;
                    return FakeProcessRunner.Success(request, "Successfully paired to 192.168.1.20:37123\n");
                }

                if (request.Arguments.Count >= 2 && request.Arguments[0] == "connect")
                {
                    return FakeProcessRunner.Success(request, "connected to 192.168.1.20:41871\n");
                }

                return FakeProcessRunner.Success(request);
            },
        };
        var store = new FakeWirelessDeviceStore();
        await using var service = CreateService(runner, store, pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await service.CompleteQrPairingAsync(session);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("Paired and connected", result.Message, StringComparison.Ordinal);
        Assert.Contains(runner.Requests, request =>
            request.Arguments.SequenceEqual(["pair", "192.168.1.20:37123", "123456"]));
        Assert.Contains(runner.Requests, request =>
            request.Arguments.SequenceEqual(["connect", "192.168.1.20:41871"]));
        var remembered = await store.ListAsync();
        Assert.Equal("192.168.1.20:41871", Assert.Single(remembered).Address);
    }

    [Fact]
    public async Task PairAsync_WrongCodeReturnsSpecificGuidance()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => IsCommand(request, "pair")
                ? FakeProcessRunner.Failure(request, "Failed: Wrong password or connection was dropped.")
                : FakeProcessRunner.Success(request),
        };
        await using var service = CreateService(runner);

        var result = await service.PairAsync("192.168.1.20:37123", "999999");

        Assert.False(result.Succeeded);
        Assert.Contains("pairing code", result.Guidance ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("adb pair", result.CommandLine ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PairAsync_UnreachableDeviceExplainsNetworkRequirements()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => IsCommand(request, "pair")
                ? FakeProcessRunner.Failure(request, "failed to connect to '192.168.1.20:37123': No route to host")
                : FakeProcessRunner.Success(request),
        };
        await using var service = CreateService(runner);

        var result = await service.PairAsync("192.168.1.20:37123", "123456");

        Assert.False(result.Succeeded);
        Assert.Contains("same Wi-Fi network", result.Guidance ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("firewall", result.Guidance ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectAsync_RemembersSuccessfulDevice()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => IsCommand(request, "connect")
                ? FakeProcessRunner.Success(request, "connected to 192.168.1.20:41871\n")
                : FakeProcessRunner.Success(request),
        };
        var store = new FakeWirelessDeviceStore();
        await using var service = CreateService(runner, store);

        var result = await service.ConnectAsync("192.168.1.20:41871", "Pixel 8");

        Assert.True(result.Succeeded, result.Message);
        var remembered = Assert.Single(service.RememberedDevices);
        Assert.Equal("192.168.1.20:41871", remembered.Address);
        Assert.Equal("Pixel 8", remembered.Name);
        Assert.NotNull(remembered.LastConnectedAt);
    }

    [Fact]
    public async Task ReconnectAsync_ReconnectsRememberedDeviceThatIsNotConnected()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                if (request.Arguments.SequenceEqual(["devices"]))
                {
                    return FakeProcessRunner.Success(request, "List of devices attached\n");
                }

                if (IsCommand(request, "connect"))
                {
                    return FakeProcessRunner.Success(request, "connected to 192.168.1.20:41871\n");
                }

                return FakeProcessRunner.Success(request);
            },
        };
        var store = new FakeWirelessDeviceStore(new WirelessDevice
        {
            Address = "192.168.1.20:41871",
            Name = "Pixel 8",
        });
        await using var service = CreateService(runner, store);

        var results = await service.ReconnectAsync();

        var result = Assert.Single(results);
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(runner.Requests, request =>
            request.Arguments.SequenceEqual(["connect", "192.168.1.20:41871"]));
    }

    [Fact]
    public async Task ReconnectAsync_SkipsDeviceThatIsAlreadyConnected()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => request.Arguments.SequenceEqual(["devices"])
                ? FakeProcessRunner.Success(
                    request,
                    "List of devices attached\n192.168.1.20:41871\tdevice\n")
                : FakeProcessRunner.Success(request),
        };
        var store = new FakeWirelessDeviceStore(new WirelessDevice
        {
            Address = "192.168.1.20:41871",
            Name = "Pixel 8",
        });
        await using var service = CreateService(runner, store);

        var results = await service.ReconnectAsync();

        Assert.Empty(results);
        Assert.DoesNotContain(runner.Requests, request => IsCommand(request, "connect"));
    }

    [Fact]
    public async Task ReconnectAsync_ReconnectsDeviceThatIsOffline()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                if (request.Arguments.SequenceEqual(["devices"]))
                {
                    return FakeProcessRunner.Success(
                        request,
                        "List of devices attached\n192.168.1.20:41871\toffline\n");
                }

                if (IsCommand(request, "connect"))
                {
                    return FakeProcessRunner.Success(request, "connected to 192.168.1.20:41871\n");
                }

                return FakeProcessRunner.Success(request);
            },
        };
        var store = new FakeWirelessDeviceStore(new WirelessDevice
        {
            Address = "192.168.1.20:41871",
            Name = "Pixel 8",
        });
        await using var service = CreateService(runner, store);

        var results = await service.ReconnectAsync();

        var result = Assert.Single(results);
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(runner.Requests, request =>
            request.Arguments.SequenceEqual(["connect", "192.168.1.20:41871"]));
    }

    [Fact]
    public async Task ReconnectAsync_UsesRediscoveredPortWhenTheRememberedPortChanged()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                if (request.Arguments.SequenceEqual(["devices"]))
                {
                    return FakeProcessRunner.Success(request, "List of devices attached\n");
                }

                if (request.Arguments.SequenceEqual(["mdns", "services"]))
                {
                    return FakeProcessRunner.Success(
                        request,
                        "List of discovered mdns services\n" +
                        "adb-ABC123-xyz _adb-tls-connect._tcp 192.168.1.20:45678\n");
                }

                if (request.Arguments.SequenceEqual(["connect", "192.168.1.20:45678"]))
                {
                    return FakeProcessRunner.Success(request, "connected to 192.168.1.20:45678\n");
                }

                if (IsCommand(request, "connect"))
                {
                    return FakeProcessRunner.Failure(
                        request,
                        "failed to connect to '192.168.1.20:41871': Connection refused");
                }

                return FakeProcessRunner.Success(request);
            },
        };
        var store = new FakeWirelessDeviceStore(new WirelessDevice
        {
            Address = "192.168.1.20:41871",
            Name = "Pixel 8",
        });
        await using var service = CreateService(runner, store);

        var results = await service.ReconnectAsync();

        var result = Assert.Single(results);
        Assert.True(result.Succeeded, result.Message);
        var remembered = Assert.Single(await store.ListAsync());
        Assert.Equal("192.168.1.20:45678", remembered.Address);
        Assert.Equal("Pixel 8", remembered.Name);
    }

    [Fact]
    public async Task DiscoverAsync_MarksConnectServicesThatAreConnected()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                if (IsCommand(request, "connect"))
                {
                    return FakeProcessRunner.Success(request, "connected to 192.168.1.20:41871\n");
                }

                if (request.Arguments.SequenceEqual(["mdns", "services"]))
                {
                    return FakeProcessRunner.Success(
                        request,
                        "List of discovered mdns services\n" +
                        "adb-ABC123-xyz _adb-tls-connect._tcp 192.168.1.20:41871\n" +
                        "adb-DEF456-xyz _adb-tls-connect._tcp 192.168.1.21:41871\n");
                }

                return FakeProcessRunner.Success(request);
            },
        };
        await using var service = CreateService(runner);
        await service.ConnectAsync("192.168.1.20:41871", remember: false);

        var services = await service.DiscoverAsync();

        Assert.Equal("Connected", services[0].StateLabel);
        Assert.Equal("Available", services[1].StateLabel);
    }

    [Fact]
    public async Task StartAsync_BeginsReconnectingWhenTheOptionIsEnabledLater()
    {
        var enabled = false;
        var reconnectObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                if (request.Arguments.SequenceEqual(["devices"]))
                {
                    reconnectObserved.TrySetResult();
                    return FakeProcessRunner.Success(request, "List of devices attached\n");
                }

                return FakeProcessRunner.Success(request);
            },
        };
        var options = new Func<WirelessReconnectOptions>(() =>
            new WirelessReconnectOptions(Enabled: enabled, Delay: TimeSpan.FromMilliseconds(20)));
        var store = new FakeWirelessDeviceStore(new WirelessDevice
        {
            Address = "192.168.1.20:41871",
            Name = "Pixel 8",
        });
        await using var service = CreateService(runner, store, options: options);

        await service.StartAsync();
        await Task.Delay(100);
        Assert.False(reconnectObserved.Task.IsCompleted);

        enabled = true;
        await reconnectObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync();
    }

    [Fact]
    public async Task DiscoverAsync_MdnsFailureSurfacesActionableError()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Failure(request, "mdns discovery failed"),
        };
        await using var service = CreateService(runner);

        var services = await service.DiscoverAsync();

        Assert.Empty(services);
        Assert.Contains("mDNS discovery failed", service.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static WirelessPairingSession CreateSession(string name, string code) => new()
    {
        ServiceName = name,
        PairingCode = code,
        QrPayload = WirelessAdbService.BuildQrPayload(name, code),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
    };

    private static bool IsCommand(ProcessRequest request, string command) =>
        request.Arguments.Count > 0 && request.Arguments[0] == command;

    private static WirelessAdbService CreateService(
        FakeProcessRunner runner,
        FakeWirelessDeviceStore? store = null,
        TimeSpan? pollInterval = null,
        Func<WirelessReconnectOptions>? options = null)
    {
        return new WirelessAdbService(
            runner,
            store ?? new FakeWirelessDeviceStore(),
            static () => "adb",
            options ?? (static () => new WirelessReconnectOptions(Enabled: false)),
            pollInterval ?? TimeSpan.FromMilliseconds(1));
    }
}
