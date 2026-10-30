using System.Runtime.InteropServices.WindowsRuntime;
using DroidDeck.Plugin.Abstractions.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DroidDeck.Plugin.UI.Rendering;

/// <summary>Shows decoded text for device and local files.</summary>
public sealed class TextFilePreviewRenderer : IRenderer
{
    /// <inheritdoc />
    public string Id => BuiltInRendererDescriptors.TextFilePreview.Id;

    /// <inheritdoc />
    public string DisplayName => BuiltInRendererDescriptors.TextFilePreview.DisplayName;

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.FilePreview;

    /// <inheritdoc />
    public int Priority => BuiltInRendererDescriptors.TextFilePreview.Priority;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) =>
        context.Data is FilePreviewData { Text: not null };

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (FilePreviewData)context.Data;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(RendererVisuals.Text(data.FileName, fontSize: 12, bold: true));
        panel.Children.Add(RendererVisuals.Text(data.Text ?? string.Empty, monospace: true));
        return RendererVisuals.Scroll(panel);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>Shows image files supplied by the host as bytes.</summary>
public sealed class ImageFilePreviewRenderer : IRenderer
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".bmp",
        ".webp",
        ".ico",
        ".tif",
        ".tiff",
    };

    /// <inheritdoc />
    public string Id => BuiltInRendererDescriptors.ImageFilePreview.Id;

    /// <inheritdoc />
    public string DisplayName => BuiltInRendererDescriptors.ImageFilePreview.DisplayName;

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.FilePreview;

    /// <inheritdoc />
    public int Priority => BuiltInRendererDescriptors.ImageFilePreview.Priority;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) =>
        context.Data is FilePreviewData { Bytes: { Length: > 0 } } data &&
        ImageExtensions.Contains(Path.GetExtension(data.FileName));

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (FilePreviewData)context.Data;
        var image = new Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            MaxHeight = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var status = RendererVisuals.Text("Loading preview...", fontSize: 11);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(RendererVisuals.Text(data.FileName, fontSize: 12, bold: true));
        panel.Children.Add(status);
        panel.Children.Add(image);

        _ = LoadAsync(data.Bytes!, image, status, context);
        return RendererVisuals.Scroll(panel);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static async Task LoadAsync(
        byte[] bytes,
        Image image,
        TextBlock status,
        IRenderContext context)
    {
        try
        {
            var bitmap = new BitmapImage();
            var stream = new MemoryStream(bytes);
            await using (stream)
            {
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            }

            await context.Dispatcher.InvokeAsync(() =>
            {
                if (context.CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                image.Source = bitmap;
                status.Visibility = Visibility.Collapsed;
            });
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        {
            await context.Dispatcher.InvokeAsync(() =>
                status.Text = $"The image preview could not be decoded: {exception.Message}");
        }
    }
}
