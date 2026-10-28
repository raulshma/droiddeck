using DroidDeck.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>Lets the user accept or dismiss changes found while refreshing detection.</summary>
public sealed partial class RefreshDiffDialog : ContentDialog
{
    /// <summary>Creates a diff review dialog.</summary>
    public RefreshDiffDialog(string projectName, ProjectRefreshDiff diff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(diff);

        InitializeComponent();
        IntroText.Text =
            $"Detection for \"{projectName}\" found {diff.Changes.Count} change(s). " +
            "Choose which detected values to apply.";
        ChangeList.ItemsSource = diff.Changes.Select(change => new ChangeRow(change)).ToArray();
    }

    /// <summary>Gets the fields the user accepted.</summary>
    public IReadOnlyCollection<string> AcceptedFields =>
        (ChangeList.ItemsSource as IEnumerable<ChangeRow>)?
        .Where(row => row.IsSelected)
        .Select(row => row.Field)
        .ToArray() ?? [];

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (AcceptedFields.Count == 0)
        {
            args.Cancel = true;
            ErrorText.Text = "Select at least one change to apply, or dismiss the dialog.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>One changed field in the review list.</summary>
    public sealed class ChangeRow
    {
        /// <summary>Creates a row from a field change.</summary>
        public ChangeRow(ProjectFieldChange change)
        {
            ArgumentNullException.ThrowIfNull(change);
            Field = change.Field;
            KindLabel = change.Kind;
            OldValue = $"Was: {Display(change.OldValue)}";
            NewValue = $"Now: {Display(change.NewValue)}";
            IsSelected = true;
        }

        /// <summary>Gets the field path.</summary>
        public string Field { get; }

        /// <summary>Gets the change kind.</summary>
        public string KindLabel { get; }

        /// <summary>Gets the previous value.</summary>
        public string OldValue { get; }

        /// <summary>Gets the detected value.</summary>
        public string NewValue { get; }

        /// <summary>Gets or sets whether the change is accepted.</summary>
        public bool IsSelected { get; set; }

        private static string Display(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }
}
