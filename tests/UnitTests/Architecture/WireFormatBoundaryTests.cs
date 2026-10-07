using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace UnitTests.Architecture;

/// <summary>
/// The wire format stops at the transport. <c>Utos.*</c>, <c>Grpc.*</c> and <c>Google.Protobuf</c>
/// — and <c>Common.Workflow.Proto</c>, which exists to wrap them — may be referenced only by a
/// <c>*.Api</c> project. Anything deeper would let a proto type reach the application layer, the
/// domain, or something persisted, and every one of those compiles.
///
/// Read from the project files, like <see cref="ModuleBoundaryTests"/>, so it asserts what the
/// graph declares. It therefore does not see packages that arrive transitively, which is how the
/// one known exception gets in: <c>Execution.Infrastructure</c> catches the Dapr client's
/// <c>RpcException</c>, and <c>Grpc.Core</c> comes with <c>Dapr.Client</c>, not from a reference.
/// </summary>
public class WireFormatBoundaryTests
{
    private static readonly string[] WirePackagePrefixes = ["Utos.", "Grpc.", "Google.Protobuf"];

    private const string ProtoBridge = "Common.Workflow.Proto";

    /// <summary>
    /// <c>tools/</c> holds the Aspire host, which starts the daemon rather than being part of it,
    /// and outside the layering this test is about.
    /// </summary>
    private static readonly string[] NotLayered = ["tools"];

    [Fact]
    public void Every_layered_project_is_actually_inspected()
    {
        // Guards the test below against a wrong SrcRoot, which would make it green forever.
        Assert.Contains(LayeredProjects(), project => Name(project) == "Execution.Domain");
    }

    [Fact]
    public void Only_api_projects_reference_the_wire_format()
    {
        var violations = (
            from project in LayeredProjects()
            let name = Name(project)
            where !name.EndsWith(".Api", StringComparison.Ordinal) && name != ProtoBridge
            from reference in WireReferences(project)
            select $"{name} -> {reference}").ToList();

        Assert.True(violations.Count == 0,
            "Proto and gRPC types stop at *.Api; map to domain types there:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> LayeredProjects() =>
        Directory.EnumerateFiles(SrcRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !NotLayered.Any(directory =>
                path.Replace('\\', '/').Contains($"/src/{directory}/", StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> WireReferences(string csproj)
    {
        var document = XDocument.Load(csproj);

        var packages = document.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")!.Value)
            .Where(package => WirePackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.Ordinal)));

        var bridge = document.Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")!.Value.Replace('\\', '/')))
            .Where(project => project == ProtoBridge);

        return packages.Concat(bridge);
    }

    private static string Name(string csproj) => Path.GetFileNameWithoutExtension(csproj);

    // Resolved from this file's own compile-time location, so the test does not depend on where
    // the runner happens to set the working directory.
    private static string SrcRoot { get; } = FindSrc();

    private static string FindSrc([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", "src"));
}
