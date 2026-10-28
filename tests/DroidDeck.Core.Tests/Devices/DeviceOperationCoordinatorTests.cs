using DroidDeck.Core.Devices;
using DroidDeck.Core.Resources;

namespace DroidDeck.Core.Tests.Devices;

public sealed class DeviceOperationCoordinatorTests
{
    [Fact]
    public async Task AcquireAsync_SerializesOperationsOnTheSameDevice()
    {
        using var coordinator = new DeviceOperationCoordinator(maxConcurrency: 4);
        var first = await coordinator.AcquireAsync("emulator-5554", "install");
        var secondCompleted = false;
        var secondTask = Task.Run(async () =>
        {
            await using var second = await coordinator.AcquireAsync("emulator-5554", "push");
            secondCompleted = true;
        });

        await Task.Delay(100);
        Assert.False(secondCompleted);
        Assert.Single(coordinator.ActiveOperations);

        await first.DisposeAsync();
        await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(secondCompleted);
        Assert.Empty(coordinator.ActiveOperations);
    }

    [Fact]
    public async Task AcquireAsync_EnforcesTheGlobalConcurrencyLimit()
    {
        using var coordinator = new DeviceOperationCoordinator(maxConcurrency: 2);
        var running = 0;
        var max = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Work(string serial)
        {
            await using var lease = await coordinator.AcquireAsync(serial, "install");
            var current = Interlocked.Increment(ref running);
            InterlockedExtensions.Max(ref max, current);
            await gate.Task;
            Interlocked.Decrement(ref running);
        }

        var tasks = Enumerable.Range(0, 4)
            .Select(index => Task.Run(() => Work($"device-{index}")))
            .ToArray();
        await Task.Delay(150);
        Assert.Equal(2, coordinator.ActiveOperations.Count);
        Assert.Equal(2, max);
        gate.SetResult();
        await Task.WhenAll(tasks);
        Assert.Empty(coordinator.ActiveOperations);
    }

    [Fact]
    public async Task MaxConcurrency_CanBeRaisedBeforeOperationsStart()
    {
        using var coordinator = new DeviceOperationCoordinator(maxConcurrency: 1)
        {
            MaxConcurrency = 3,
        };

        var leases = new List<IAsyncDisposable>();
        for (var index = 0; index < 3; index++)
        {
            leases.Add(await coordinator.AcquireAsync($"device-{index}", "install"));
        }

        Assert.Equal(3, coordinator.ActiveOperations.Count);
        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task MaxConcurrency_CanBeRaisedWhileAnOperationIsActive()
    {
        using var coordinator = new DeviceOperationCoordinator(maxConcurrency: 1);
        var first = await coordinator.AcquireAsync("device-a", "install");
        var secondTask = coordinator.AcquireAsync("device-b", "install");
        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted);

        coordinator.MaxConcurrency = 2;

        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, coordinator.ActiveOperations.Count);
        await second.DisposeAsync();
        await first.DisposeAsync();
    }

    [Fact]
    public async Task MaxConcurrency_CanBeLoweredWhileOperationsAreActive()
    {
        using var coordinator = new DeviceOperationCoordinator(maxConcurrency: 2);
        var first = await coordinator.AcquireAsync("device-a", "install");
        var second = await coordinator.AcquireAsync("device-b", "install");
        coordinator.MaxConcurrency = 1;

        var thirdTask = coordinator.AcquireAsync("device-c", "install");
        await Task.Delay(50);
        Assert.False(thirdTask.IsCompleted);

        await first.DisposeAsync();
        await Task.Delay(50);
        Assert.False(thirdTask.IsCompleted);

        await second.DisposeAsync();
        var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(coordinator.ActiveOperations);
        await third.DisposeAsync();
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)))
            {
                Interlocked.CompareExchange(ref target, value, current);
            }
        }
    }
}

public sealed class ResourceGuardTests
{
    [Fact]
    public async Task TryAcquire_BlocksAtTheHardLimitAndExplainsConsumers()
    {
        using var guard = new ResourceGuard(new ResourceLimits
        {
            MaxMirrors = 1,
            MaxRecordings = 1,
            MaxBuilds = 1,
            WarnAtPercent = 100,
        });
        Assert.True(guard.TryAcquire(ResourceKind.Mirror, "Pixel 8", out var lease));
        Assert.False(guard.TryAcquire(ResourceKind.Mirror, "Pixel 7", out _));
        Assert.Contains("Pixel 8", guard.Explain());
        Assert.Contains("limit", guard.Explain(), StringComparison.OrdinalIgnoreCase);

        await lease!.DisposeAsync();
        Assert.True(guard.TryAcquire(ResourceKind.Mirror, "Pixel 7", out var second));
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task WarningRaised_NamesTheConsumersBeforeTheLimit()
    {
        using var guard = new ResourceGuard(new ResourceLimits
        {
            MaxMirrors = 4,
            WarnAtPercent = 50,
        });
        var warnings = new List<ResourceWarningEventArgs>();
        guard.WarningRaised += (_, args) => warnings.Add(args);

        var first = await guard.AcquireAsync(ResourceKind.Mirror, "Pixel 8");
        var second = await guard.AcquireAsync(ResourceKind.Mirror, "Pixel 7");

        var warning = Assert.Single(warnings);
        Assert.Equal(ResourceKind.Mirror, warning.Kind);
        Assert.Equal(2, warning.Count);
        Assert.Contains("Pixel 8", warning.Message);
        Assert.Contains("Pixel 7", warning.Message);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }
}
