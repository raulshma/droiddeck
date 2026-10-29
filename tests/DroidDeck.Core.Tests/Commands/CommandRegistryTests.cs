using DroidDeck.Core.Commands;

namespace DroidDeck.Core.Tests.Commands;

public sealed class CommandRegistryTests
{
    [Fact]
    public void RegisterAddsTheCommandAndRaisesChanged()
    {
        var registry = new CommandRegistry();
        var changed = 0;
        registry.Changed += (_, _) => changed++;
        var definition = Create("shell.test", "Test command");

        registry.Register(definition);

        Assert.Same(definition, registry.Find("shell.test"));
        Assert.Equal([definition], registry.Commands);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void CommandsReturnsASnapshotThatLaterChangesDoNotMutate()
    {
        var registry = new CommandRegistry();
        registry.Register(Create("shell.first", "First"));

        var snapshot = registry.Commands;
        registry.Register(Create("shell.second", "Second"));

        Assert.Single(snapshot);
        Assert.Equal(2, registry.Commands.Count);
    }

    [Fact]
    public void RegisteringADuplicateIdentifierIsRejected()
    {
        var registry = new CommandRegistry();
        registry.Register(Create("shell.duplicate", "First"));

        Assert.Throws<ArgumentException>(() => registry.Register(Create("shell.duplicate", "Second")));
    }

    [Fact]
    public void UnregisterRemovesTheCommandAndRaisesChanged()
    {
        var registry = new CommandRegistry();
        registry.Register(Create("shell.temporary", "Temporary"));
        var changed = 0;
        registry.Changed += (_, _) => changed++;

        Assert.True(registry.Unregister("shell.temporary"));
        Assert.False(registry.Unregister("shell.temporary"));
        Assert.Null(registry.Find("shell.temporary"));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void RegistrationValidatesTheDefinition()
    {
        var registry = new CommandRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
        Assert.Throws<ArgumentException>(() => registry.Register(Create(string.Empty, "No identifier")));
        Assert.Throws<ArgumentException>(() => registry.Register(Create("   ", "Whitespace identifier")));
        Assert.Throws<ArgumentException>(() => registry.Register(Create("shell.no-title", string.Empty)));
        Assert.Throws<ArgumentNullException>(() =>
            registry.Register(new CommandDefinition("shell.no-action", "No action", "Test", null!)));
    }

    [Fact]
    public void IsEnabledFallsBackToTrueWithoutAPredicate()
    {
        var enabled = Create("shell.enabled", "Enabled");
        var disabled = Create("shell.disabled", "Disabled") with
        {
            IsEnabledPredicate = () => false,
        };

        Assert.True(enabled.IsEnabled);
        Assert.False(disabled.IsEnabled);
    }

    private static CommandDefinition Create(
        string id,
        string title,
        Func<CancellationToken, Task>? execute = null) =>
        new(id, title, "Test", execute ?? (_ => Task.CompletedTask));
}
