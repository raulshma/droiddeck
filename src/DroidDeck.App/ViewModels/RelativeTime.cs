using System.Globalization;

namespace DroidDeck.App.ViewModels;

/// <summary>Formats timestamps and durations the way the shell describes them.</summary>
public static class RelativeTime
{
    /// <summary>Describes how long ago a timestamp was.</summary>
    public static string Ago(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 45)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 2)
        {
            return "1 minute ago";
        }

        if (delta.TotalMinutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalMinutes} minutes ago");
        }

        if (delta.TotalHours < 2)
        {
            return "1 hour ago";
        }

        if (delta.TotalHours < 24)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalHours} hours ago");
        }

        if (delta.TotalDays < 2)
        {
            return "yesterday";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalDays} days ago");
    }

    /// <summary>Describes an elapsed duration as a short clock reading.</summary>
    public static string Span(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m");
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalSeconds}s");
    }
}
