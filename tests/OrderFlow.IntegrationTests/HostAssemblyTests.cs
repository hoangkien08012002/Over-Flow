using System.Reflection;
using Xunit;

namespace OrderFlow.IntegrationTests;

public sealed class HostAssemblyTests
{
    [Theory]
    [InlineData("OrderFlow.Api")]
    [InlineData("OrderFlow.Worker")]
    public void Executable_hosts_have_entry_points(string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));

        Assert.NotNull(assembly.EntryPoint);
    }
}
