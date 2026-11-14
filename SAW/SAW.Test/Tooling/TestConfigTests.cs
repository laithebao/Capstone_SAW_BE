using SAW.Testing;

namespace SAW.Test.Tooling;

public sealed class TestConfigTests
{
    [Fact]
    public void EmptyLocalTemplateDoesNotHideEnvironmentSecret()
    {
        var key = "SAW_CONFIG_TEST_" + Guid.NewGuid().ToString("N");
        var file = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable(key, "provided-by-CI");
            File.WriteAllText(file, key + "=''\n");
            Assert.Equal("provided-by-CI", TestConfig.Load(file)[key]);
            File.WriteAllText(file, key + "='explicit-local-value'\n");
            Assert.Equal("explicit-local-value", TestConfig.Load(file)[key]);
        }
        finally { Environment.SetEnvironmentVariable(key, null); File.Delete(file); }
    }

    [Theory]
    [InlineData("EDGE_PATH=\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"", "EDGE_PATH", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe")]
    [InlineData("export SECRET='a;b=c#$HOME$(echo example)'", "SECRET", "a;b=c#$HOME$(echo example)")]
    [InlineData("EMPTY=''", "EMPTY", "")]
    [InlineData("EMPTY=", "EMPTY", "")]
    [InlineData("\uFEFF# comment\r\nNAME=hello\r\n", "NAME", "hello")]
    public void EnvValuesRemainLiteralAcrossShellsAndOperatingSystems(string input, string name, string expected)
    {
        Assert.Equal(expected, TestConfig.Parse(input)[name]);
    }

    [Theory]
    [InlineData("missing equals")]
    [InlineData("BAD KEY=value")]
    [InlineData("VALUE='unclosed")]
    public void InvalidConfigFailsBeforeStartingExternalProcesses(string input) =>
        Assert.Throws<FormatException>(() => TestConfig.Parse(input));
}
