using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var arguments = Arguments.Parse(args);
var artifactRoot = Path.GetFullPath(arguments.Required("artifact-root"));
var metadataRoot = Path.Combine(artifactRoot, "release-metadata");
var nativeRoot = Path.Combine(artifactRoot, "native-metadata");
Directory.CreateDirectory(metadataRoot);
Directory.CreateDirectory(nativeRoot);

var releaseAssets = CollectAssets(artifactRoot);
WriteNativeMetadata(nativeRoot, releaseAssets, arguments);
var assets = CollectAssets(artifactRoot);
var components = CollectLockfileComponents();
WriteHashes(metadataRoot, assets, artifactRoot);
WriteSboms(metadataRoot, assets, artifactRoot, arguments, components);
WriteProvenance(metadataRoot, assets, artifactRoot, arguments);
WritePublicKey(metadataRoot, arguments.Required("private-key"));

static List<string> CollectAssets(string artifactRoot) =>
    Directory.EnumerateFiles(artifactRoot, "*", SearchOption.AllDirectories)
        .Where(path => !Relative(artifactRoot, path).StartsWith("release-metadata/", StringComparison.Ordinal))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToList() is var assets && assets.Count > 0
            ? assets
            : throw new InvalidOperationException($"No immutable release assets found in {artifactRoot}");

static List<Component> CollectLockfileComponents()
{
    var dotnetRoot = FindDotnetRoot();
    var components = new Dictionary<string, Component>(StringComparer.Ordinal);

    foreach (var project in new[] { "src/Dsf.Cli", "src/Dsf.Core" })
    {
        var lockfile = Path.Combine(dotnetRoot, project, "packages.lock.json");
        if (!File.Exists(lockfile))
        {
            continue;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(lockfile));
        if (!document.RootElement.TryGetProperty("dependencies", out var frameworks))
        {
            continue;
        }

        foreach (var framework in frameworks.EnumerateObject())
        {
            foreach (var dependency in framework.Value.EnumerateObject())
            {
                var value = dependency.Value;
                if (value.GetProperty("type").GetString() == "Project")
                {
                    continue;
                }

                components[dependency.Name] = new Component(
                    dependency.Name,
                    value.TryGetProperty("resolved", out var resolved) ? resolved.GetString() ?? "" : "",
                    value.TryGetProperty("contentHash", out var contentHash) ? contentHash.GetString() ?? "" : "");
            }
        }
    }

    return components.Values.OrderBy(component => component.Name, StringComparer.Ordinal).ToList();
}

static void WriteHashes(string metadataRoot, List<string> assets, string artifactRoot)
{
    var lines = assets.Select(path => $"{Hash(path)}  {Relative(artifactRoot, path)}");
    File.WriteAllText(Path.Combine(metadataRoot, "SHA256SUMS"), string.Join('\n', lines) + '\n');
}

static void WriteSboms(
    string metadataRoot,
    List<string> assets,
    string artifactRoot,
    Arguments arguments,
    List<Component> components)
{
    foreach (var asset in assets)
    {
        var relative = Relative(artifactRoot, asset);
        const string rootSpdxId = "SPDXRef-Package-dsf-cli";
        var packages = new List<object>
        {
            Package(relative, rootSpdxId, arguments.Required("version"), Hash(asset)),
        };
        packages.AddRange(components.Select(component => Package(
            component.Name,
            ComponentSpdxId(component.Name),
            string.IsNullOrEmpty(component.Version) ? "NOASSERTION" : component.Version,
            component.ContentHash,
            string.IsNullOrEmpty(component.Version)
                ? "NOASSERTION"
                : $"https://www.nuget.org/packages/{component.Name}/{component.Version}")));

        var relationships = new List<object>
        {
            new
            {
                spdxElementId = "SPDXRef-DOCUMENT",
                relationshipType = "DESCRIBES",
                relatedSpdxElement = rootSpdxId,
            },
        };
        relationships.AddRange(components.Select(component => new
        {
            spdxElementId = rootSpdxId,
            relationshipType = "DEPENDS_ON",
            relatedSpdxElement = ComponentSpdxId(component.Name),
        }));

        var sbom = new
        {
            spdxVersion = "SPDX-2.3",
            dataLicense = "CC0-1.0",
            SPDXID = "SPDXRef-DOCUMENT",
            name = $"dsf-cli-{arguments.Required("version")}-{relative}",
            documentNamespace =
                $"https://github.com/{arguments.Required("repository")}/releases/{arguments.Required("version")}/{relative}",
            creationInfo = new
            {
                created = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                creators = new[] { "Tool: dotnet/eng/ReleaseMetadataGenerator" },
            },
            packages,
            relationships,
        };

        var sbomPath = Path.Combine(metadataRoot, $"{SafeName(relative)}.spdx.json");
        File.WriteAllText(sbomPath, JsonSerializer.Serialize(sbom, Serialization.Options) + '\n');
        RunOpenSsl(
            "pkeyutl",
            "-sign",
            "-rawin",
            "-inkey",
            arguments.Required("private-key"),
            "-in",
            sbomPath,
            "-out",
            Path.Combine(metadataRoot, $"{SafeName(relative)}.spdx.json.sig"));
    }
}

static object Package(string name, string spdxId, string version, string hash, string downloadLocation = "NOASSERTION") =>
    new
    {
        name,
        SPDXID = spdxId,
        versionInfo = version,
        downloadLocation,
        filesAnalyzed = false,
        checksums = string.IsNullOrEmpty(hash)
            ? Array.Empty<object>()
            : new object[] { new { algorithm = "SHA256", checksumValue = hash } },
        licenseConcluded = "NOASSERTION",
        licenseDeclared = "NOASSERTION",
        copyrightText = "NOASSERTION",
    };

static void WriteProvenance(string metadataRoot, List<string> assets, string artifactRoot, Arguments arguments)
{
    var provenance = new
    {
        buildType = "https://github.com/dark-software-factory/dotnet-cli-release/v1",
        builder = new { id = "github-actions" },
        invocation = new { runId = arguments.Required("run-id") },
        metadata = new
        {
            repository = arguments.Required("repository"),
            commit = arguments.Required("commit"),
            version = arguments.Required("version"),
        },
        subjects = assets.Select(path => new
        {
            name = Relative(artifactRoot, path),
            digest = new { sha256 = Hash(path) },
        }),
    };
    File.WriteAllText(
        Path.Combine(metadataRoot, "provenance.json"),
        JsonSerializer.Serialize(provenance, Serialization.Options) + '\n');
}

static void WritePublicKey(string metadataRoot, string privateKey) =>
    RunOpenSsl(
        "pkey",
        "-in",
        privateKey,
        "-pubout",
        "-out",
        Path.Combine(metadataRoot, "release-verification-key.pem"));

static void WriteNativeMetadata(string nativeRoot, List<string> assets, Arguments arguments)
{
    var byName = assets.ToDictionary(
        path => Path.GetFileName(path) ?? throw new InvalidOperationException($"Asset has no file name: {path}"),
        Hash,
        StringComparer.Ordinal);
    var releaseUrl =
        $"https://github.com/{arguments.Required("repository")}/releases/download/v{arguments.Required("version")}";
    var linuxX64 = FirstHash(byName, "linux-x64");
    var winX64 = FirstHash(byName, "win-x64");
    var osxArm64 = FirstHash(byName, "osx-arm64");

    File.WriteAllText(Path.Combine(nativeRoot, "winget-portable.yaml"), $$"""
        PackageIdentifier: DarkSoftwareFactory.Cli
        PackageVersion: {{arguments.Required("version")}}
        Installers:
          - Architecture: x64
            InstallerType: portable
            InstallerUrl: {{releaseUrl}}/dsf-cli-win-x64.zip
            InstallerSha256: {{winX64}}
        ManifestType: installer
        ManifestVersion: 1.9.0
        """);
    File.WriteAllText(Path.Combine(nativeRoot, "homebrew-cask.rb"), $$"""
        cask "dsf-cli" do
          version "{{arguments.Required("version")}}"
          sha256 arm: "{{osxArm64}}"
          url "{{releaseUrl}}/dsf-cli-osx-arm64.tar.gz"
          name "Dark Software Factory CLI"
          binary "dsf"
        end
        """);
    File.WriteAllText(Path.Combine(nativeRoot, "debian-control"), $$"""
        Package: dsf-cli
        Version: {{arguments.Required("version")}}
        Architecture: amd64
        Maintainer: Dark Software Factory
        Description: Dark Software Factory CLI
        SHA256: {{linuxX64}}
        """);
    File.WriteAllText(Path.Combine(nativeRoot, "rpm.spec"), $$"""
        Name: dsf-cli
        Version: {{arguments.Required("version")}}
        Release: 1
        Summary: Dark Software Factory CLI
        License: NOASSERTION
        Source0: {{releaseUrl}}/dsf-cli-linux-x64.tar.gz
        # OpenPGP: sign this source metadata before manual RPM repository submission.
        %description
        Dark Software Factory CLI.
        """);
}

static string FindDotnetRoot()
{
    for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
    {
        var candidate = Path.Combine(current.FullName, "dotnet");
        if (File.Exists(Path.Combine(candidate, "Dsf.sln")))
        {
            return candidate;
        }
    }

    throw new DirectoryNotFoundException("Could not find the dotnet workspace.");
}

static void RunOpenSsl(params string[] arguments)
{
    var startInfo = new ProcessStartInfo("openssl")
    {
        UseShellExecute = false,
        RedirectStandardError = true,
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start openssl.");
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"openssl failed: {process.StandardError.ReadToEnd()}");
    }
}

static string Hash(string path) =>
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static string Relative(string root, string path) =>
    Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

static string ComponentSpdxId(string name) => $"SPDXRef-Package-{SafeName(name)}";

static string FirstHash(IReadOnlyDictionary<string, string> hashes, string token) =>
    hashes.FirstOrDefault(pair => pair.Key.Contains(token, StringComparison.Ordinal)).Value?.ToUpperInvariant()
        ?? "NOASSERTION";

static string SafeName(string value) =>
    string.Join("", value.Select(character => char.IsLetterOrDigit(character) ? character : '-')).Trim('-');

sealed record Component(string Name, string Version, string ContentHash);

static class Serialization
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}

sealed class Arguments(Dictionary<string, string> values)
{
    public static Arguments Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 == arguments.Length)
            {
                throw new ArgumentException("Expected --name value arguments.");
            }

            values.Add(arguments[index][2..], arguments[index + 1]);
        }

        return new Arguments(values);
    }

    public string Required(string name) =>
        values.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"Missing required --{name} argument.");
}
