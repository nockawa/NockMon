using System.Globalization;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Model;

namespace MyCpuMonitor.Core.Tests;

public class NamePatternTests
{
    [Theory]
    [InlineData("devenv.exe", "devenv.exe", true)]
    [InlineData("DEVENV.EXE", "devenv.exe", true)]
    [InlineData("devenv", "devenv.exe", true)]
    [InlineData("devenv", "devenv.com", false)]
    [InlineData("dev", "devenv.exe", false)]
    [InlineData("chrome*", "chrome.exe", true)]
    [InlineData("*host*", "svchost.exe", true)]
    [InlineData("svc?ost.exe", "svchost.exe", true)]
    [InlineData("a*b*c", "aXbYbZc", true)]
    [InlineData("a*b*c", "aXbYbZ", false)]
    [InlineData("*", "anything", true)]
    [InlineData("code*; devenv", "devenv.exe", true)]
    [InlineData("code*; devenv", "notepad.exe", false)]
    [InlineData("", "a.exe", false)]
    public void Matches(string pattern, string name, bool expected) =>
        Assert.Equal(expected, new NamePattern(pattern).IsMatch(name));
}

public class MetricFormatterTests
{
    public MetricFormatterTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(2.0, 8, 25.0)]
    [InlineData(1.0, 0, 0.0)]
    public void CpuPercent(double cpus, int logical, double expected) =>
        Assert.Equal(expected, MetricFormatter.CpuPercent(cpus, logical), 9);

    [Theory]
    [InlineData(CpuScale.Machine, 2.0, "6.3 %")]
    [InlineData(CpuScale.Cpus, 2.0, "2.00 CPUs")]
    [InlineData(CpuScale.Cpus, 0.071, "0.07 CPUs")]
    [InlineData(CpuScale.Cpus, 12.44, "12.4 CPUs")]
    public void Cpu(CpuScale scale, double cpus, string expected) =>
        Assert.Equal(expected, MetricFormatter.Format(cpus, MetricUnit.Cpu, scale, 32));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(12.3 * 1024 * 1024, "12.3 MB")]
    [InlineData(456.0 * 1024 * 1024 * 1024, "456 GB")]
    [InlineData(-2048, "-2.00 KB")]
    public void Bytes(double value, string expected) => Assert.Equal(expected, MetricFormatter.FormatBytes(value));

    [Fact]
    public void Units()
    {
        Assert.Equal("50.0 %", MetricFormatter.Format(2, MetricUnit.Cpu, CpuScale.Machine, 4));
        Assert.Equal("2.00 CPUs", MetricFormatter.Format(2, MetricUnit.Cpu, CpuScale.Cpus, 4));
        Assert.Equal("1,234/s", MetricFormatter.Format(1234, MetricUnit.PerSecond, CpuScale.Machine, 4));
        Assert.Equal("2.5/s", MetricFormatter.Format(2.5, MetricUnit.PerSecond, CpuScale.Machine, 4));
        Assert.Equal("1.00 KB/s", MetricFormatter.Format(1024, MetricUnit.BytesPerSecond, CpuScale.Machine, 4));
        Assert.Equal("", MetricFormatter.Format(double.NaN, MetricUnit.Count, CpuScale.Machine, 4));
    }
}
