using TwoTicks.Core;

namespace TwoTicks.Core.Tests;

public class AppPathsTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("twoticks-paths").FullName;

    public void Dispose() => Directory.Delete(_parent, recursive: true);

    private string Former => Path.Combine(_parent, AppPaths.FormerName);

    private string Current => Path.Combine(_parent, AppName.Own);

    [Fact]
    public void ANewInstallGetsTheFolderOfTheNewName()
    {
        Assert.Equal(Current, AppPaths.TakeOver(_parent));
        Assert.False(Directory.Exists(Former));
    }

    [Fact]
    public void TheFolderOfTheFormerNameComesAlong()
    {
        Directory.CreateDirectory(Path.Combine(Former, "media"));
        File.WriteAllText(Path.Combine(Former, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(Former, "media", "photo.jpg"), "picture");

        Assert.Equal(Current, AppPaths.TakeOver(_parent));

        Assert.False(Directory.Exists(Former));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Current, "settings.json")));
        Assert.Equal("picture", File.ReadAllText(Path.Combine(Current, "media", "photo.jpg")));
    }

    [Fact]
    public void AFolderOfTheNewNameIsNotReplaced()
    {
        Directory.CreateDirectory(Former);
        File.WriteAllText(Path.Combine(Former, "settings.json"), "old");
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "settings.json"), "new");

        Assert.Equal(Current, AppPaths.TakeOver(_parent));

        Assert.Equal("new", File.ReadAllText(Path.Combine(Current, "settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Former, "settings.json")));
    }

    [Fact]
    public void AFolderStillInUseStaysWhereItIsForNow()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows refuses to rename a folder with an open file in it.
            return;
        }
        Directory.CreateDirectory(Former);
        string database = Path.Combine(Former, "chats.db");
        using (File.Open(database, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(Former, AppPaths.TakeOver(_parent));
            Assert.False(Directory.Exists(Current));
        }

        // The next start finds it free.
        Assert.Equal(Current, AppPaths.TakeOver(_parent));
        Assert.True(File.Exists(Path.Combine(Current, "chats.db")));
    }

    [Fact]
    public void TheNameFollowsTheIcon()
    {
        Assert.Equal("TwoTicks", AppName.Of(whatsApp: false));
        Assert.Equal("WhatsApp", AppName.Of(whatsApp: true));
    }
}
