using System.Xml.Linq;
using Xunit;

namespace OrderFlow.UnitTests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Runtime_projects_follow_the_documented_dependency_direction()
    {
        var root = FindRepositoryRoot();
        var expected = new Dictionary<string, string[]>
        {
            ["OrderFlow.Domain"] = [],
            ["OrderFlow.Application"] = ["OrderFlow.Domain"],
            ["OrderFlow.Infrastructure"] = ["OrderFlow.Application", "OrderFlow.Domain"],
            ["OrderFlow.Api"] = ["OrderFlow.Application", "OrderFlow.Infrastructure"],
            ["OrderFlow.Worker"] = ["OrderFlow.Application", "OrderFlow.Infrastructure"]
        };

        foreach (var (projectName, allowedReferences) in expected)
        {
            var projectPath = Path.Combine(root, "src", projectName, $"{projectName}.csproj");
            var project = XDocument.Load(projectPath);
            var references = project.Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => include is not null)
                .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(allowedReferences.Order(StringComparer.Ordinal), references);
        }
    }

    [Fact]
    public void Domain_has_no_framework_or_infrastructure_packages()
    {
        var projectPath = Path.Combine(FindRepositoryRoot(), "src", "OrderFlow.Domain", "OrderFlow.Domain.csproj");
        var project = XDocument.Load(projectPath);
        var externalReferences = project.Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "FrameworkReference")
            .ToArray();

        Assert.Empty(externalReferences);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OrderFlow.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("OrderFlow.sln was not found above the test output directory.");
    }
}
