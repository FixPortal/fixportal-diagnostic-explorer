using System.Diagnostics;
using System.Text;
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

        string owner = Guid.NewGuid().ToString("N");
        bool owned = false;
        BuildLifetime lifetime = new();
        Exception? primary = null;
        try
        {
            if (Directory.Exists(outDir) || File.Exists(outDir))
            {
                throw new IOException("Refusing an existing build output path.");
            }
            Directory.CreateDirectory(outDir);
            using (FileStream witness = new(Path.Combine(outDir, ".de-build-owner"), FileMode.CreateNew))
            using (StreamWriter writer = new(witness))
            {
                await writer.WriteAsync(owner);
            }
            owned = true;
            await RunDotnetBuild(csprojPath, configuration, outDir, lifetime);

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
                .BeEquivalentTo("http://*:2803", "http://*:6001");

            diagnosticExplorer.GetProperty("Uri").GetString().Should().Be("http://localhost:6001/diagnostics");
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                if (owned)
                {
                    if (!lifetime.ExitAndReadersObserved)
                    {
                        throw new IOException(
                            "Refusing output deletion before the owned root process and its readers have been observed."
                        );
                    }
                    DeleteOwnedOutput(outDir, owner);
                }
            }
            catch (Exception error)
            {
                Console.WriteLine("BuildOutputCleanupFailure: " + error);
                if (primary is null)
                {
                    throw;
                }
            }
        }
    }

    private static void DeleteOwnedOutput(string outDir, string owner)
    {
        string root = Path.GetFullPath(outDir);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (
            !string.Equals(Path.GetDirectoryName(root), temp, comparison)
            || !Path.GetFileName(root).StartsWith("de-g1-", StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileName(root)[6..], "N", out _)
        )
        {
            throw new IOException("Refusing cleanup outside the exact owned GUID output root.");
        }
        string witness = Path.Combine(root, ".de-build-owner");
        if (
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(witness) & FileAttributes.ReparsePoint) != 0
        )
        {
            throw new IOException("Refusing redirected output ownership paths.");
        }
        if (File.ReadAllText(witness) != owner)
        {
            throw new IOException("Refusing build output cleanup without its ownership witness.");
        }
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.TryPop(out string? path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Refusing reparse points in owned build output.");
            }
            if (Directory.Exists(path))
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                {
                    pending.Push(child);
                }
            }
        }
        Console.WriteLine("Deleting verified owned build output: " + root);
        Directory.Delete(root, recursive: true);
    }

    private sealed class BuildLifetime
    {
        public bool ExitAndReadersObserved { get; set; }
    }

    private static async Task RunDotnetBuild(
        string csprojPath,
        string configuration,
        string outDir,
        BuildLifetime lifetime
    )
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
                "--disable-build-servers",
                $"-p:OutDir={Path.TrimEndingDirectorySeparator(outDir)}{Path.DirectorySeparatorChar}",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        using var process =
            Process.Start(startInfo) ?? throw new InvalidOperationException("The owned dotnet build did not start.");
        Task<string> stdoutTask = DrainBounded(process.StandardOutput);
        Task<string> stderrTask = DrainBounded(process.StandardError);
        Exception? primary = null;
        try
        {
            Task exit = process.WaitForExitAsync(deadline.Token);
            List<Task> pending = [exit, stdoutTask, stderrTask];
            while (pending.Count != 0)
            {
                Task completed = await Task.WhenAny(pending).WaitAsync(deadline.Token);
                pending.Remove(completed);
                await completed; // A failed/overflowing reader triggers cleanup immediately.
            }
            process
                .ExitCode.Should()
                .Be(
                    0,
                    $"dotnet build ({configuration}) should succeed.\nstdout:\n{await stdoutTask}\nstderr:\n{await stderrTask}"
                );
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await FinishOwnedBuild(process, stdoutTask, stderrTask, lifetime, primary);
        }
    }

    private static async Task FinishOwnedBuild(
        Process process,
        Task<string> stdoutTask,
        Task<string> stderrTask,
        BuildLifetime lifetime,
        Exception? primary
    )
    {
        using CancellationTokenSource cleanupDeadline = new(TimeSpan.FromSeconds(30));
        List<Exception> cleanup = [];
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The owned process exited between the liveness check and Kill.
        }
        catch (Exception error)
        {
            cleanup.Add(error);
        }
        try
        {
            await process.WaitForExitAsync(cleanupDeadline.Token);
        }
        catch (Exception error)
        {
            cleanup.Add(error);
        }
        foreach (Task reader in new Task[] { stdoutTask, stderrTask })
        {
            try
            {
                await reader.WaitAsync(cleanupDeadline.Token);
            }
            catch (Exception error)
            {
                if (!ReferenceEquals(error, primary))
                {
                    cleanup.Add(error);
                }
            }
        }
        lifetime.ExitAndReadersObserved = cleanup.Count == 0;
        foreach (Exception error in cleanup)
        {
            Console.WriteLine("BuildChildCleanupFailure: " + error);
        }
        if (primary is null && cleanup.Count != 0)
        {
            throw new AggregateException("BuildChildCleanupFailure", cleanup);
        }
    }

    private static async Task<string> DrainBounded(StreamReader reader)
    {
        // 1,048,576 UTF-16 code units per stream; at most 4,194,304 payload bytes across both.
        const int maxCharacters = 1024 * 1024;
        char[] buffer = new char[4096];
        StringBuilder output = new();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            if (count > maxCharacters - output.Length)
            {
                throw new IOException("Owned build output exceeded the explicit per-stream character cap.");
            }
            output.Append(buffer, 0, count);
        }
        return output.ToString();
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
