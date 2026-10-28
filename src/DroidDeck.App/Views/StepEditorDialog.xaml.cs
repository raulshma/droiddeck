using DroidDeck.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>Edits one custom shell step.</summary>
public sealed partial class StepEditorDialog : ContentDialog
{
    /// <summary>Creates a step editor for a new or existing step.</summary>
    public StepEditorDialog(RunStepDefinition? step = null)
    {
        InitializeComponent();
        FailurePolicyCombo.ItemsSource = Enum.GetValues<StepFailurePolicy>();
        Step = Copy(step);
        NameBox.Text = Step.Name;
        CommandBox.Text = Step.Command ?? string.Empty;
        ArgumentsBox.Text = string.Join(Environment.NewLine, Step.Arguments);
        WorkingDirectoryBox.Text = Step.WorkingDirectory ?? string.Empty;
        EnvironmentBox.Text = string.Join(
            Environment.NewLine,
            Step.Environment.Select(pair => $"{pair.Key}={pair.Value}"));
        ConditionBox.Text = Step.Condition ?? string.Empty;
        FailurePolicyCombo.SelectedItem = Step.FailurePolicy;
        RetryBox.Value = Step.RetryCount;
        TimeoutBox.Value = Step.TimeoutSeconds;
        EnabledCheck.IsChecked = Step.Enabled;
    }

    /// <summary>Gets the edited step. It is only applied after the dialog is accepted.</summary>
    public RunStepDefinition Step { get; }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var command = CommandBox.Text.Trim();
        if (command.Length == 0)
        {
            args.Cancel = true;
            ErrorText.Text = "A command is required.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Step.Name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? Path.GetFileNameWithoutExtension(command)
            : NameBox.Text.Trim();
        Step.Command = command;
        Step.Arguments = ArgumentsBox.Text
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();
        Step.WorkingDirectory = NullIfEmpty(WorkingDirectoryBox.Text);
        Step.Environment = ParseEnvironment(EnvironmentBox.Text);
        Step.Condition = NullIfEmpty(ConditionBox.Text);
        Step.FailurePolicy = FailurePolicyCombo.SelectedItem is StepFailurePolicy policy
            ? policy
            : StepFailurePolicy.Stop;
        Step.RetryCount = double.IsNaN(RetryBox.Value) ? 1 : (int)Math.Round(RetryBox.Value);
        Step.TimeoutSeconds = double.IsNaN(TimeoutBox.Value) ? 300 : (int)Math.Round(TimeoutBox.Value);
        Step.Enabled = EnabledCheck.IsChecked == true;
        Step.Kind = RunStepKind.Custom;
    }

    private static RunStepDefinition Copy(RunStepDefinition? step)
    {
        if (step is null)
        {
            return new RunStepDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Custom step",
            };
        }

        return new RunStepDefinition
        {
            Id = string.IsNullOrWhiteSpace(step.Id) ? Guid.NewGuid().ToString("N") : step.Id,
            Name = step.Name,
            Kind = step.Kind,
            Command = step.Command,
            Arguments = [.. step.Arguments],
            WorkingDirectory = step.WorkingDirectory,
            Environment = new Dictionary<string, string>(step.Environment, StringComparer.OrdinalIgnoreCase),
            TimeoutSeconds = step.TimeoutSeconds,
            Condition = step.Condition,
            FailurePolicy = step.FailurePolicy,
            RetryCount = step.RetryCount,
            Enabled = step.Enabled,
        };
    }

    private static Dictionary<string, string> ParseEnvironment(string text)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0)
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
