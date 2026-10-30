using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.PluginPack;

/// <summary>The outcome of packaging one plugin folder.</summary>
/// <param name="PackagePath">The absolute path of the written <c>.ddplugin</c> archive.</param>
/// <param name="PluginId">The plugin id from the manifest.</param>
/// <param name="Version">The plugin version from the manifest.</param>
/// <param name="SizeInBytes">The package size in bytes.</param>
/// <param name="Sha256">The lowercase SHA-256 hash of the package.</param>
/// <param name="Entries">The archive entry names in sorted order.</param>
public sealed record PluginPackageResult(
    string PackagePath,
    string PluginId,
    string Version,
    long SizeInBytes,
    string Sha256,
    IReadOnlyList<string> Entries);

/// <summary>Thrown when a plugin folder cannot be packaged.</summary>
public sealed class PluginPackException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    public PluginPackException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    public PluginPackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Packages a built plugin folder as a <c>.ddplugin</c> archive that the catalog
/// installer accepts. Assemblies the host already provides are left out so a
/// package stays small and a plugin cannot shadow a host contract.
/// </summary>
public static partial class PluginPackager
{
    /// <summary>The package extension the host installer accepts.</summary>
    public const string PackageExtension = ".ddplugin";

    /// <summary>The largest compressed package the host accepts.</summary>
    public const long MaxPackageBytes = 8 * 1024 * 1024;

    /// <summary>The largest extracted size the host accepts.</summary>
    public const long MaxExtractedBytes = 64 * 1024 * 1024;

    /// <summary>Builds do not preserve meaningful timestamps inside the package.</summary>
    private static readonly DateTimeOffset FixedEntryTimestamp =
        new(1990, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly HashSet<string> HostProvidedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "WinRT.Runtime.dll",
    };

    private static readonly string[] HostProvidedPrefixes =
    [
        "DroidDeck.Plugin.",
        "Microsoft.WindowsAppSDK.",
        "Microsoft.WindowsAppRuntime.",
        "Microsoft.Windows.SDK.NET.",
        "Microsoft.WinUI.",
        "Microsoft.Web.WebView2.",
        "Microsoft.InteractiveExperiences.",
    ];

    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex(
        @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>
    /// Packages a plugin folder into <paramref name="outputDirectory"/>.
    /// </summary>
    /// <param name="pluginDirectory">The build output folder that contains <c>plugin.json</c>.</param>
    /// <param name="outputDirectory">The directory that receives the package.</param>
    /// <param name="includeSymbols">Whether to keep portable symbol files.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<PluginPackageResult> CreateAsync(
        string pluginDirectory,
        string outputDirectory,
        bool includeSymbols = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var root = Path.GetFullPath(pluginDirectory);
        if (!Directory.Exists(root))
        {
            throw new PluginPackException($"The plugin directory '{root}' does not exist.");
        }

        var manifest = await ReadManifestAsync(root, cancellationToken).ConfigureAwait(false);
        ValidateManifest(manifest, root);

        var files = CollectFiles(root, includeSymbols);
        if (!files.Any(file => string.Equals(file.EntryName, "plugin.json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PluginPackException($"The plugin directory '{root}' has no plugin.json at its root.");
        }

        if (!files.Any(file => string.Equals(file.EntryName, manifest.EntryAssembly, StringComparison.Ordinal)))
        {
            throw new PluginPackException(
                $"The entry assembly '{manifest.EntryAssembly}' is a host-provided file and cannot be packaged.");
        }

        var outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        var packagePath = Path.Combine(outputRoot, $"{manifest.Id}-{manifest.Version}{PackageExtension}");

        await WriteArchiveAsync(packagePath, files, cancellationToken).ConfigureAwait(false);

        var size = new FileInfo(packagePath).Length;
        if (size > MaxPackageBytes)
        {
            File.Delete(packagePath);
            throw new PluginPackException(
                $"The package is {size} bytes; the host accepts at most {MaxPackageBytes} bytes compressed.");
        }

        string sha256;
        await using (var stream = File.OpenRead(packagePath))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            sha256 = Convert.ToHexStringLower(hash);
        }

        return new PluginPackageResult(
            packagePath,
            manifest.Id,
            manifest.Version,
            size,
            sha256,
            files.Select(file => file.EntryName).ToArray());
    }

    private static async Task<PluginManifest> ReadManifestAsync(
        string pluginDirectory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(pluginDirectory, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            throw new PluginPackException($"No plugin.json was found in '{pluginDirectory}'.");
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer
                .DeserializeAsync(stream, PluginJsonContext.Default.PluginManifest, cancellationToken)
                .ConfigureAwait(false);
            return manifest ?? throw new PluginPackException($"'{manifestPath}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new PluginPackException($"Could not parse '{manifestPath}': {exception.Message}", exception);
        }
    }

    private static void ValidateManifest(PluginManifest manifest, string pluginDirectory)
    {
        if (!PluginIdPattern().IsMatch(manifest.Id))
        {
            throw new PluginPackException(
                $"The plugin id '{manifest.Id}' must be lowercase and contain only letters, digits, dots, and dashes.");
        }

        if (!VersionPattern().IsMatch(manifest.Version))
        {
            throw new PluginPackException($"The plugin version '{manifest.Version}' is not a semantic version.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
            !string.Equals(Path.GetFileName(manifest.EntryAssembly), manifest.EntryAssembly, StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(manifest.EntryAssembly), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginPackException(
                $"The entry assembly '{manifest.EntryAssembly}' must be a .dll file name at the package root.");
        }

        if (!File.Exists(Path.Combine(pluginDirectory, manifest.EntryAssembly)))
        {
            throw new PluginPackException(
                $"The entry assembly '{manifest.EntryAssembly}' was not found in '{pluginDirectory}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            throw new PluginPackException("The manifest entry type is required.");
        }
    }

    private static PackageFile[] CollectFiles(string pluginDirectory, bool includeSymbols)
    {
        var files = new List<PackageFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long extractedBytes = 0;

        foreach (var path in Directory.EnumerateFiles(pluginDirectory, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(path);
            if (!includeSymbols && string.Equals(Path.GetExtension(fileName), ".pdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsHostProvided(fileName))
            {
                continue;
            }

            var entryName = Path.GetRelativePath(pluginDirectory, path).Replace('\\', '/');
            if (!seen.Add(entryName))
            {
                throw new PluginPackException(
                    $"The plugin directory contains two files that map to the archive entry '{entryName}'.");
            }

            var length = new FileInfo(path).Length;
            extractedBytes = checked(extractedBytes + length);
            if (extractedBytes > MaxExtractedBytes)
            {
                throw new PluginPackException(
                    $"The extracted plugin exceeds the host limit of {MaxExtractedBytes} bytes.");
            }

            files.Add(new PackageFile(path, entryName));
        }

        return files.OrderBy(file => file.EntryName, StringComparer.Ordinal).ToArray();
    }

    private static bool IsHostProvided(string fileName)
    {
        if (HostProvidedFileNames.Contains(fileName))
        {
            return true;
        }

        foreach (var prefix in HostProvidedPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WriteArchiveAsync(
        string packagePath,
        PackageFile[] files,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            packagePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(file.EntryName, CompressionLevel.Optimal);
            entry.LastWriteTime = FixedEntryTimestamp;
            await using var source = File.OpenRead(file.Path);
            await using var target = entry.Open();
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record PackageFile(string Path, string EntryName);
}
