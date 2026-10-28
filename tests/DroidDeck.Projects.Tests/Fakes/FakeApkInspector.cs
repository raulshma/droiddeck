using DroidDeck.Core.Runs;

namespace DroidDeck.Projects.Tests.Fakes;

internal sealed class FakeApkInspector : IApkInspector
{
    private readonly Dictionary<string, ApkInfo> _infos = new(StringComparer.OrdinalIgnoreCase);

    public string? Aapt2Path => "aapt2";

    public void Add(string path, ApkInfo info)
    {
        info.Path = path;
        _infos[Path.GetFullPath(path)] = info;
    }

    public Task<ApkInspectionResult> InspectAsync(string apkPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_infos.TryGetValue(Path.GetFullPath(apkPath), out var info)
            ? new ApkInspectionResult { Info = info }
            : new ApkInspectionResult
            {
                Error = "The APK could not be read.",
                Guidance = "Use a valid APK.",
            });
    }

    public async Task<IReadOnlyList<ApkInspectionResult>> InspectManyAsync(
        IReadOnlyList<string> apkPaths,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ApkInspectionResult>(apkPaths.Count);
        foreach (var path in apkPaths)
        {
            results.Add(await InspectAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }
}
