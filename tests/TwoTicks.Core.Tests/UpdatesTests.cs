using TwoTicks.Core;

namespace TwoTicks.Core.Tests;

public class UpdatesTests
{
    private const string Release = """
        {
          "tag_name": "v1.3.0",
          "html_url": "https://github.com/rathlinus/WinWhatsApp/releases/tag/v1.3.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "TwoTicks-1.3.0-x64.zip", "size": 10, "browser_download_url": "https://example.com/zip" },
            { "name": "TwoTicks-1.3.0-Setup.exe", "size": 1234, "digest": "sha256:ABCDEF", "browser_download_url": "https://example.com/setup" }
          ]
        }
        """;

    [Fact]
    public void FindsTheSetupProgramOfANewerRelease()
    {
        UpdateInfo? update = Updates.Parse(Release, new Version(1, 2, 0, 0));
        Assert.NotNull(update);
        Assert.Equal(new Version(1, 3, 0), update.Version);
        Assert.Equal("https://example.com/setup", update.SetupUrl);
        Assert.Equal(1234, update.SetupSize);
        Assert.Equal("abcdef", update.Sha256);
    }

    [Theory]
    [InlineData(1, 3, 0, 0)]
    [InlineData(1, 4, 0, 0)]
    public void IgnoresReleasesThatAreNotNewer(int major, int minor, int build, int revision) =>
        Assert.Null(Updates.Parse(Release, new Version(major, minor, build, revision)));

    [Fact]
    public void IgnoresPrereleases() =>
        Assert.Null(Updates.Parse(Release.Replace("\"prerelease\": false", "\"prerelease\": true"), new Version(1, 0, 0)));

    [Fact]
    public void IgnoresReleasesWithoutASetupProgram() =>
        Assert.Null(Updates.Parse(Release.Replace("Setup.exe", "Setup.msi"), new Version(1, 0, 0)));
}
