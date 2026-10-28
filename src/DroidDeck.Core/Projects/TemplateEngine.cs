using System.Text;
using System.Text.RegularExpressions;

namespace DroidDeck.Core.Projects;

/// <summary>Values available while expanding placeholders and conditions.</summary>
public sealed class RunTemplateContext
{
    /// <summary>Gets or sets the project root.</summary>
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the project name.</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Gets or sets the run configuration name.</summary>
    public string ConfigurationName { get; set; } = string.Empty;

    /// <summary>Gets or sets the device serial.</summary>
    public string? DeviceSerial { get; set; }

    /// <summary>Gets or sets the device model.</summary>
    public string? DeviceModel { get; set; }

    /// <summary>Gets or sets the device API level.</summary>
    public int? DeviceApiLevel { get; set; }

    /// <summary>Gets or sets whether the device is an emulator.</summary>
    public bool IsEmulator { get; set; }

    /// <summary>Gets or sets the application ID.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>Gets or sets the selected variant.</summary>
    public string? Variant { get; set; }

    /// <summary>Gets or sets the selected module.</summary>
    public string? Module { get; set; }

    /// <summary>Gets or sets the resolved APK path.</summary>
    public string? ApkPath { get; set; }

    /// <summary>Gets or sets the launcher activity.</summary>
    public string? Activity { get; set; }

    /// <summary>Gets or sets the build task.</summary>
    public string? BuildTask { get; set; }

    /// <summary>Gets or sets additional environment values.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a context for a project.</summary>
    public static RunTemplateContext ForProject(ProjectModel project, RunConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(configuration);
        return new RunTemplateContext
        {
            ProjectDirectory = project.RootPath,
            ProjectName = project.Name,
            ConfigurationName = configuration.Name,
            ApplicationId = configuration.Launch.PackageName ?? project.ApplicationId,
            Variant = configuration.VariantName,
            Module = configuration.ModuleName,
            Activity = configuration.Launch.Activity ?? project.LauncherActivity,
            BuildTask = configuration.Build.Task,
        };
    }
}

/// <summary>Expands placeholders and evaluates simple step conditions.</summary>
public static partial class TemplateEngine
{
    private static readonly Regex PlaceholderPattern = PlaceholderRegex();
    private static readonly Regex ConditionComparisonPattern = ComparisonRegex();

    /// <summary>Expands <c>${name}</c> and <c>${env:NAME}</c> placeholders.</summary>
    public static string Expand(string? template, RunTemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return PlaceholderPattern.Replace(template, match =>
        {
            var name = match.Groups[1].Value.Trim();
            return Resolve(name, context) ?? match.Value;
        });
    }

    /// <summary>Expands placeholders in every item of a sequence.</summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string> templates, RunTemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(templates);
        return templates.Select(template => Expand(template, context)).ToArray();
    }

    /// <summary>Resolves a single placeholder name.</summary>
    public static string? Resolve(string name, RunTemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (name.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var variable = name[4..].Trim();
            if (context.Environment.TryGetValue(variable, out var value))
            {
                return value;
            }

            return Environment.GetEnvironmentVariable(variable);
        }

        return name.ToLowerInvariant() switch
        {
            "projectdir" or "projectdirectory" or "projectroot" => context.ProjectDirectory,
            "projectname" => context.ProjectName,
            "configurationname" or "configname" => context.ConfigurationName,
            "deviceserial" or "serial" => context.DeviceSerial,
            "devicemodel" or "model" => context.DeviceModel,
            "deviceapilevel" or "apilevel" => context.DeviceApiLevel?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "applicationid" or "package" or "packagename" => context.ApplicationId,
            "variant" => context.Variant,
            "module" => context.Module,
            "apkpath" => context.ApkPath,
            "activity" => context.Activity,
            "buildtask" or "task" => context.BuildTask,
            "isemulator" => context.IsEmulator ? "true" : "false",
            _ => null,
        };
    }

    /// <summary>
    /// Evaluates a step condition. An empty condition is true. Supported forms are
    /// <c>true</c>, <c>false</c>, <c>exists:path</c>, <c>device:emulator</c>,
    /// <c>device:physical</c>, <c>variant:debug</c>, and <c>a == b</c>/<c>a != b</c>.
    /// </summary>
    public static bool EvaluateCondition(
        string? condition,
        RunTemplateContext context,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        reason = null;
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        var expanded = Expand(condition, context).Trim();
        if (bool.TryParse(expanded, out var literal))
        {
            reason = literal ? null : "The step condition is false.";
            return literal;
        }

        if (expanded.StartsWith("exists:", StringComparison.OrdinalIgnoreCase))
        {
            var path = expanded[7..].Trim().Trim('"');
            if (path.Length == 0)
            {
                reason = "The exists condition has no path.";
                return false;
            }

            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(context.ProjectDirectory, path);
            }

            var exists = File.Exists(path) || Directory.Exists(path);
            reason = exists ? null : $"Required path does not exist: {path}";
            return exists;
        }

        if (expanded.StartsWith("device:", StringComparison.OrdinalIgnoreCase))
        {
            var expected = expanded[7..].Trim();
            var actual = context.IsEmulator ? "emulator" : "physical";
            var matches = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) ||
                          (expected.Equals("device", StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrEmpty(context.DeviceSerial));
            reason = matches ? null : $"The connected device is {actual}, not {expected}.";
            return matches;
        }

        if (expanded.StartsWith("variant:", StringComparison.OrdinalIgnoreCase))
        {
            var expected = expanded[8..].Trim();
            var matches = context.Variant?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true;
            reason = matches ? null : $"The selected variant \"{context.Variant}\" does not match \"{expected}\".";
            return matches;
        }

        var comparison = ConditionComparisonPattern.Match(expanded);
        if (comparison.Success)
        {
            var left = comparison.Groups[1].Value.Trim().Trim('"');
            var right = comparison.Groups[2].Value.Trim().Trim('"');
            var equals = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            var result = comparison.Groups[3].Value == "==" ? equals : !equals;
            reason = result ? null : $"Condition failed: {left} {comparison.Groups[3].Value} {right}.";
            return result;
        }

        reason = $"The condition \"{expanded}\" could not be understood.";
        return false;
    }

    /// <summary>Formats a command line for previews and logs.</summary>
    public static string FormatCommandLine(string fileName, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder(fileName);
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(static character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^(.+?)\s*(==|!=)\s*(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();
}
