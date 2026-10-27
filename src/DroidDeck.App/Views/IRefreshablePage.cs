namespace DroidDeck.App.Views;

/// <summary>A page that refreshes its data when navigated to.</summary>
public interface IRefreshablePage
{
    /// <summary>Refreshes the page content.</summary>
    void Refresh();
}
