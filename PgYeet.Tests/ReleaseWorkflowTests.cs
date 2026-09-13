using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace PgYeet.Tests;

public sealed class ReleaseWorkflowTests : IDisposable
{
    private const string PackageName = "PgYeet.1.0.0.nupkg";
    private const string SymbolName = "PgYeet.1.0.0.snupkg";
    private readonly string _directory = Directory.CreateTempSubdirectory("pgyeet-release-test-").FullName;
    private readonly Dictionary<string, string> _environment;

    public ReleaseWorkflowTests()
    {
        File.WriteAllText(Path.Combine(_directory, "state.json"), "{\"releases\":[]}");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh.sh"), Path.Combine(_directory, "gh"));
        File.WriteAllText(Path.Combine(_directory, "curl"),
            "#!/bin/sh\nprintf '%s' \"${FAKE_HTTP_STATUS:-404}\"\n");
        if (!OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "gh", "curl" })
                File.SetUnixFileMode(Path.Combine(_directory, name),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _environment = new Dictionary<string, string>
        {
            ["PATH"] = _directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["FAKE_RELEASE_ROOT"] = _directory,
            ["PACKAGE_NAME"] = PackageName,
            ["SYMBOL_NAME"] = SymbolName,
            ["VERSION"] = "1.0.0",
            ["GITHUB_REPOSITORY"] = "jecacs/PgYeet",
            ["GITHUB_REF"] = "refs/tags/v1.0.0",
            ["GITHUB_REF_NAME"] = "v1.0.0",
            ["GITHUB_SHA"] = new string('a', 40),
            ["GITHUB_OUTPUT"] = Path.Combine(_directory, "output"),
            ["RUNNER_TEMP"] = _directory,
            ["GH_TOKEN"] = "fake",
        };
        WriteCandidate("original");
    }

    [Fact]
    public async Task First_run_preserves_attested_candidate()
    {
        await RunStep();
        Assert.Equal(ReadAsset("candidate", PackageName), ReadAsset("canonical", PackageName));
        Assert.True(ReadState()["releases"]![0]!["draft"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Retry_recovers_original_bytes_instead_of_repacked_candidate()
    {
        await RunStep();
        var original = ReadAsset("canonical", PackageName);
        WriteCandidate("repacked");
        await RunStep();
        Assert.Equal(original, ReadAsset("canonical", PackageName));
    }

    [Fact]
    public async Task Invalid_symbols_attestation_blocks_staging()
    {
        var path = Path.Combine(_directory, "candidate", SymbolName);
        File.WriteAllText(path, "tampered");
        File.WriteAllText(path + ".sha256", $"{Hash(File.ReadAllBytes(path))}  {SymbolName}\n");
        await RunStep(success: false);
        Assert.Empty(ReadState()["releases"]!.AsArray());
    }

    [Fact]
    public async Task Incomplete_draft_is_not_overwritten()
    {
        await RunStep();
        Mutate(state => state["releases"]![0]!["assets"]!.AsArray().RemoveAt(0));
        var original = ReadState().ToJsonString();
        await RunStep(success: false);
        Assert.Equal(original, ReadState().ToJsonString());
    }

    [Fact]
    public async Task Wrong_source_commit_is_rejected()
    {
        await RunStep();
        Mutate(state => state["releases"]![0]!["target_commitish"] = new string('b', 40));
        await RunStep(success: false);
    }

    [Fact]
    public async Task Tampered_canonical_package_is_not_replaced_on_retry()
    {
        await RunStep();
        var asset = Path.Combine(_directory, "201");
        File.WriteAllText(asset, "tampered canonical package");
        await RunStep(success: false);
        Assert.Equal("tampered canonical package", File.ReadAllText(asset));
    }

    [Fact]
    public async Task Ambiguous_releases_are_rejected()
    {
        await RunStep();
        Mutate(state => state["releases"]!.AsArray().Add(state["releases"]![0]!.DeepClone()));
        await RunStep(success: false);
    }

    [Theory]
    [InlineData("200")]
    [InlineData("503")]
    public async Task Only_unused_nuget_version_authorizes_new_staging(string status)
    {
        _environment["FAKE_HTTP_STATUS"] = status;
        await RunStep(success: false);
        Assert.Empty(ReadState()["releases"]!.AsArray());
    }

    [Fact]
    public async Task Publish_and_retry_use_the_verified_release_id()
    {
        await RunStep();
        Directory.Move(Path.Combine(_directory, "canonical"), Path.Combine(_directory, "artifacts"));
        _environment["RELEASE_ID"] = "101";
        _environment["PACKAGE_SHA256"] = Hash(ReadAsset("artifacts", PackageName));
        _environment["SYMBOL_SHA256"] = Hash(ReadAsset("artifacts", SymbolName));
        await RunStep("Verify and publish GitHub Release");
        await RunStep("Verify and publish GitHub Release");
        Assert.False(ReadState()["releases"]![0]!["draft"]!.GetValue<bool>());
        await RunStep();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteCandidate(string content)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory, "candidate")).FullName;
        var hashes = new JsonObject();
        foreach (var name in new[] { PackageName, SymbolName })
        {
            var data = Encoding.UTF8.GetBytes(content + name);
            File.WriteAllBytes(Path.Combine(directory, name), data);
            hashes[name] = Hash(data);
            File.WriteAllText(Path.Combine(directory, name + ".sha256"), $"{Hash(data)}  {name}\n");
        }
        File.WriteAllText(Path.Combine(directory, PackageName + ".sigstore.json"), hashes.ToJsonString());
    }

    private async Task RunStep(string name = "Stage or recover exact release assets", bool success = true)
    {
        var workflow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "release.yml"));
        var step = workflow.Split($"      - name: {name}\n", 2, StringSplitOptions.None)[1];
        var body = step.Split("        run: |\n", 2, StringSplitOptions.None)[1];
        var script = string.Join('\n', body.Split('\n')
            .TakeWhile(line => line.Length == 0 || line.StartsWith("          ", StringComparison.Ordinal))
            .Select(line => line.Length == 0 ? line : line[10..]));
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-euo", "pipefail", "-c", script })
            start.ArgumentList.Add(argument);
        foreach (var (key, value) in _environment)
            start.Environment[key] = value;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(success == (process.ExitCode == 0), await output + await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private byte[] ReadAsset(string directory, string name)
        => File.ReadAllBytes(Path.Combine(_directory, directory, name));

    private JsonNode ReadState() => JsonNode.Parse(File.ReadAllText(Path.Combine(_directory, "state.json")))!;

    private void Mutate(Action<JsonNode> change)
    {
        var state = ReadState();
        change(state);
        File.WriteAllText(Path.Combine(_directory, "state.json"), state.ToJsonString());
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
