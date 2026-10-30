using System.Diagnostics;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Sdk;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Packs the SDK and template packages, installs the templates into an isolated
/// dotnet-new hive, then generates, builds, and loads one plugin per template.
/// This is the acceptance test for "a new plugin from the template builds and
/// loads without edits". It shells out to the .NET CLI, so it runs in a
/// dedicated category rather than with the normal unit suite.
/// </summary>
[Trait("Category", "Template")]
public sealed class PluginTemplatesTests
{
    private static readonly TemplateCase[] Templates =
    [
        new("droiddeck-plugin-command", "CommandSample", "com.example.tpl.command", "command"),
        new("droiddeck-plugin-device-action", "DeviceActionSample", "com.example.tpl.deviceaction", "device-action"),
        new("droiddeck-plugin-page", "PageSample", "com.example.tpl.page", "page"),
        new("droiddeck-plugin-renderer", "RendererSample", "com.example.tpl.renderer", "renderer"),
    ];

    [Fact]
    public async Task EveryTemplateBuildsAndLoadsWithoutEdits()
    {
        using var temp = new TempDirectory("droiddeck-template-tests");
        var repository = RepositoryPaths.FindRepositoryRoot();
        var version = $"1.0.0-template.{DateTime.UtcNow:yyyyMMddHHmmss}";
        var feed = temp.CreateDirectory("feed");
        var hive = temp.CreateDirectory("template-hive");
        var generatedRoot = temp.CreateDirectory("generated");

        await PackSdkAsync(repository, feed, version);
        var templatePackage = Directory
            .GetFiles(feed, "DroidDeck.Plugin.Templates.*.nupkg")
            .Single();
        await RunDotnetAsync(repository, "new", "install", templatePackage, "--debug:custom-hive", hive);

        foreach (var template in Templates)
        {
            var projectDirectory = Path.Combine(generatedRoot, template.ProjectName);
            var projectPath = Path.Combine(projectDirectory, template.ProjectName + ".csproj");
            await RunDotnetAsync(
                repository,
                "new",
                template.ShortName,
                "-n",
                template.ProjectName,
                "-o",
                projectDirectory,
                "--pluginId",
                template.PluginId,
                "--sdkVersion",
                version,
                "--debug:custom-hive",
                hive);

            Assert.True(File.Exists(projectPath), $"'{projectPath}' was not generated.");
            WriteNuGetConfig(projectDirectory, feed);
            var projectText = await File.ReadAllTextAsync(projectPath);
            Assert.DoesNotContain("__DROIDDECK_", projectText);
            Assert.DoesNotContain(
                "com.example.myplugin",
                await File.ReadAllTextAsync(Path.Combine(projectDirectory, "plugin.json")));

            await RunDotnetAsync(
                projectDirectory,
                "restore",
                projectPath);
            await RunDotnetAsync(
                projectDirectory,
                "build",
                projectPath,
                "-c",
                "Release",
                "--no-restore");

            var outputDirectory = Assert.Single(
                Directory.GetFiles(
                    Path.Combine(projectDirectory, "bin", "Release"),
                    "plugin.json",
                    SearchOption.AllDirectories));
            outputDirectory = Path.GetDirectoryName(outputDirectory)!;

            var manifest = await PluginManifestReader.ReadAsync(outputDirectory);
            Assert.True(manifest.Succeeded, Describe(manifest));
            Assert.Equal(template.PluginId, manifest.Manifest!.Id);

            var registry = new PluginRegistry();
            await using var loader = new PluginLoader(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");
            var load = await loader.LoadAsync(outputDirectory);
            Assert.True(load.Succeeded, Describe(load));
            Assert.Equal(template.PluginId, load.Manifest!.Id);

            AssertTemplateContribution(template, registry);
        }
    }

    private static void AssertTemplateContribution(TemplateCase template, PluginRegistry registry)
    {
        switch (template.Kind)
        {
            case "command":
                var command = Assert.Single(registry.Commands);
                Assert.Equal(template.PluginId, command.PluginId);
                Assert.Equal(template.PluginId + ".command", command.Value.Id);
                break;
            case "device-action":
                var action = Assert.Single(registry.DeviceActions);
                Assert.Equal(template.PluginId, action.PluginId);
                Assert.Equal(template.PluginId + ".deviceAction", action.Value.Id);
                Assert.True(action.Value.RequiresConfirmation);
                break;
            case "page":
                var page = Assert.Single(registry.NavigationPages);
                Assert.Equal(template.PluginId, page.PluginId);
                Assert.Equal(template.PluginId + ".page", page.Value.Id);
                Assert.Equal(template.ProjectName, page.Value.Title);
                break;
            case "renderer":
                var renderer = Assert.Single(registry.Renderers);
                Assert.Equal(template.PluginId, renderer.PluginId);
                Assert.Equal(
                    $"{template.ProjectName}.{template.ProjectName}Renderer",
                    renderer.RendererType.FullName);
                break;
            default:
                throw new InvalidOperationException($"Unknown template kind '{template.Kind}'.");
        }
    }

    private static async Task PackSdkAsync(string repository, string feed, string version)
    {
        await RunDotnetAsync(
            repository,
            "pack",
            Path.Combine("src", "DroidDeck.Plugin.Abstractions", "DroidDeck.Plugin.Abstractions.csproj"),
            "-c",
            "Release",
            $"-p:PackageVersion={version}",
            "-o",
            feed);
        await RunDotnetAsync(
            repository,
            "pack",
            Path.Combine("src", "DroidDeck.Plugin.UI", "DroidDeck.Plugin.UI.csproj"),
            "-c",
            "Release",
            $"-p:PackageVersion={version}",
            "-o",
            feed);
        await RunDotnetAsync(
            repository,
            "pack",
            Path.Combine("templates", "DroidDeck.Plugin.Templates", "DroidDeck.Plugin.Templates.csproj"),
            "-c",
            "Release",
            $"-p:PackageVersion={version}",
            "-o",
            feed);
    }

    private static void WriteNuGetConfig(string directory, string feed)
    {
        File.WriteAllText(
            Path.Combine(directory, "NuGet.config"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local-feed" value="{feed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);
    }

    private static async Task RunDotnetAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the dotnet CLI.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new XunitException(
                $"dotnet {string.Join(' ', arguments)} failed with exit code {process.ExitCode}." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }

    private static string Describe(PluginManifestReadResult read) =>
        string.Join(Environment.NewLine, read.Validation.Issues.Select(issue => issue.ToString()));

    private static string Describe(PluginLoadResult result) =>
        string.Join(Environment.NewLine, result.Validation.Issues.Select(issue => issue.ToString()));

    private sealed record TemplateCase(
        string ShortName,
        string ProjectName,
        string PluginId,
        string Kind);
}
