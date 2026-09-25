using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace DiagnosticService.UnitTests;

/// <summary>
///     G1: the service's Debug and Release builds apply their own SlowCheetah transform of
///     <c>Config/settings.json</c>, selecting the intended SPA directory, listener URLs and
///     diagnostic hub URI for that configuration. <c>ProgramHostedTests</c> only exercises
///     in-memory configuration overrides and never reads the build's own transformed output, so
///     a removed or swapped transform would go undetected. This invokes the real MSBuild for each
///     configuration into an isolated output directory and reads back what it actually emitted.
/// </summary>
public sealed class BuildConfigurationTransformTests
{
    [Theory]
    [InlineData("Debug", "http://localhost:4201", "..\\..\\..\\..\\..\\diagnostics-web\\dist\\diagnostics-web")]
    [InlineData("Release", null, "diagnostics-web")]
    public async Task Build_EmitsConfigurationSpecificSettings(
        string configuration,
        string? expectedSpaProxy,
        string expectedSpaDirectory
    )
    {
        var csprojPath = Path.Combine(FindRepoRoot(), "src", "DiagnosticService", "Diagnostic.Service.csproj");
        var outDir = Path.Combine(Path.GetTempPath(), "de-g1-" + Guid.NewGuid().ToString("N"));

        try
        {
            await RunDotnetBuild(csprojPath, configuration, outDir);

            var settingsPath = Path.Combine(outDir, "Config", "settings.json");
            File.Exists(settingsPath)
                .Should()
                .BeTrue($"the build should emit a transformed settings.json for {configuration}");

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken)
            );
            JsonElement diagServiceSettings = document.RootElement.GetProperty("DiagServiceSettings");
            JsonElement diagnosticExplorer = document.RootElement.GetProperty("DiagnosticExplorer");

            diagServiceSettings.GetProperty("SpaDirectory").GetString().Should().Be(expectedSpaDirectory);
            diagServiceSettings.GetProperty("UseSpaProxy").GetBoolean().Should().Be(expectedSpaProxy != null);

            if (expectedSpaProxy != null)
            {
                diagServiceSettings.GetProperty("SpaProxy").GetString().Should().Be(expectedSpaProxy);
            }

            diagServiceSettings
                .GetProperty("Urls")
                .EnumerateArray()
                .Select(url => url.GetString())
                .Should()
                .Contain(["http://*:2803", "http://*:6001"]);

            diagnosticExplorer.GetProperty("Uri").GetString().Should().Be("http://localhost:6001/diagnostics");
        }
        finally
        {
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
        }
    }

    private static async Task RunDotnetBuild(string csprojPath, string configuration, string outDir)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            ArgumentList =
            {
                "build",
                csprojPath,
                "--configuration",
                configuration,
                "--no-restore",
                $"-p:OutDir={outDir}\\",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        process
            .ExitCode.Should()
            .Be(0, $"dotnet build ({configuration}) should succeed.\nstdout:\n{stdout}\nstderr:\n{stderr}");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DiagnosticExplorer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate DiagnosticExplorer.slnx above " + AppContext.BaseDirectory
            );
    }
}
