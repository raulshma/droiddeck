using DroidDeck.Core.Runs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>Picks the timeline template for run steps and crash events.</summary>
public sealed class TimelineTemplateSelector : DataTemplateSelector
{
    /// <summary>Gets or sets the template for run steps.</summary>
    public DataTemplate? StepTemplate { get; set; }

    /// <summary>Gets or sets the template for crash events.</summary>
    public DataTemplate? CrashTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        item is SessionPage.CrashRow ? CrashTemplate : StepTemplate;
}
