using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace UnitTests.Architecture;

/// <summary>
/// The module graph, asserted as a fact rather than left as a convention. The two edges this
/// forbids were each added by someone reaching for a type one <c>using</c> away, and both
/// compiled — nothing but this notices.
///
/// Read from the project files rather than from compiled metadata: the compiler drops assembly
/// references nothing uses, so reflection would assert what happens to be used today and would
/// pass on a dead <c>ProjectReference</c> left behind. The rule is about what the graph says.
/// </summary>
public class ModuleBoundaryTests
{
    private static readonly string[] Modules = ["definition", "execution", "observability"];

    /// <summary>
    /// Guards the test below against passing because it found nothing to check — a wrong
    /// <see cref="SrcRoot"/> would make it green forever while asserting exactly nothing.
    /// </summary>
    [Fact]
    public void Every_module_is_actually_inspected()
    {
        var found = Directory.EnumerateFiles(SrcRoot, "*.csproj", SearchOption.AllDirectories)
            .Select(ModuleOf)
            .Where(module => module is not null)
            .Distinct()
            .ToList();

        Assert.Equal(Modules.Order(), found.Order()!);
    }

    [Fact]
    public void No_module_references_another_module()
    {
        var violations = (
            from project in Directory.EnumerateFiles(SrcRoot, "*.csproj", SearchOption.AllDirectories)
            let owner = ModuleOf(project)
            where owner is not null
            from reference in ProjectReferences(project)
            let referenced = ModuleOf(reference)
            where referenced is not null && referenced != owner
            select $"{Name(project)} -> {Name(reference)}").ToList();

        Assert.True(violations.Count == 0,
            "Modules talk through common/, not to each other:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> ProjectReferences(string csproj) =>
        XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")!.Value.Replace('\\', '/'))
            .Select(include => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, include)));

    /// <summary>Which module a path belongs to, or null for <c>common/</c>, <c>api/</c> and <c>tools/</c>.</summary>
    private static string? ModuleOf(string path) =>
        Modules.FirstOrDefault(module =>
            path.Replace('\\', '/').Contains($"/src/{module}/", StringComparison.OrdinalIgnoreCase));

    private static string Name(string csproj) => Path.GetFileNameWithoutExtension(csproj);

    // Resolved from this file's own compile-time location, so the test does not depend on where
    // the runner happens to set the working directory.
    private static string SrcRoot { get; } = FindSrc();

    private static string FindSrc([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", "src"));
}
