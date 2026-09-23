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
    private static readonly string[] Modules = ["blobs", "definition", "execution", "observability"];

    /// <summary>Directories under <c>src/</c> that are shared rather than a bounded context.</summary>
    private static readonly string[] NotModules = ["common", "api", "tools"];

    /// <summary>
    /// Guards the test below against passing because it found nothing to check — a wrong
    /// <see cref="SrcRoot"/> would make it green forever while asserting exactly nothing.
    /// <para>
    /// Read from the <em>directory layout</em> rather than by filtering paths through
    /// <see cref="Modules"/>, which is what it used to do. That version could only ever notice a
    /// module that had been removed: <c>ModuleOf</c> returns a name from the list or null, so a
    /// module the list did not mention was invisible, and adding one left it outside the boundary
    /// check below with nothing failing. A new module now has to be declared here before the suite
    /// is green, which is the point — the check is worthless on the modules it does not know about.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_module_is_actually_inspected()
    {
        var found = Directory.EnumerateDirectories(SrcRoot)
            .Select(directory => Path.GetFileName(directory)!)
            .Where(name => !NotModules.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(Modules.Order(), found.Order());
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
