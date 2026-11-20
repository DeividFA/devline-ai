using System.Reflection;
using Orq.Core;

namespace Orq.Tests;

public class CliVersionTests
{
    [Fact]
    public async Task Version_PrintsProductAndFourPartVersionWithoutRepositorySetup()
    {
        var cli = typeof(global::Program).Assembly;
        var result = await Proc.RunAsync("dotnet",
            new[] { cli.Location, "--version", "--repo", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) },
            Path.GetTempPath(), null, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(result.Ok, result.All);
        Assert.Equal("DevLine AI 1.0.0.0", result.Stdout.Trim());
        Assert.Equal("", result.Stderr);
        foreach (var assembly in new[] { cli, typeof(OrqConfig).Assembly })
        {
            Assert.Equal(new Version(1, 0, 0, 0), assembly.GetName().Version);
            Assert.Equal("1.0.0.0", assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
            Assert.Equal("DevLine AI", assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
        }
    }
}
