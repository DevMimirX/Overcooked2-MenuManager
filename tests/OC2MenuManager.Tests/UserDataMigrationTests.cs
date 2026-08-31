using OC2MenuManager.Infrastructure;
using Xunit;

namespace OC2MenuManager.Tests;

public sealed class UserDataMigrationTests : IDisposable
{
    private readonly string tempRoot;

    public UserDataMigrationTests()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "oc2-menu-manager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void CopiesFirstExistingSourceInPriorityOrder()
    {
        string missingSource = Path.Combine(tempRoot, "missing.cfg");
        string preferredSource = WriteFile("preferred.cfg", "preferred");
        string fallbackSource = WriteFile("fallback.cfg", "fallback");
        string destination = Path.Combine(tempRoot, "nested", "current.cfg");

        bool copied = UserDataMigration.CopyFirstExistingWhenDestinationMissing(
            destination,
            new[] { missingSource, preferredSource, fallbackSource });

        Assert.True(copied);
        Assert.Equal("preferred", File.ReadAllText(destination));
    }

    [Fact]
    public void DoesNotOverwriteExistingDestination()
    {
        string destination = WriteFile("current.cfg", "current");
        string legacySource = WriteFile("legacy.cfg", "legacy");

        bool copied = UserDataMigration.CopyFirstExistingWhenDestinationMissing(
            destination,
            new[] { legacySource });

        Assert.False(copied);
        Assert.Equal("current", File.ReadAllText(destination));
    }

    [Fact]
    public void LeavesDestinationAbsentWhenNoLegacyFileExists()
    {
        string destination = Path.Combine(tempRoot, "current.cfg");

        bool copied = UserDataMigration.CopyFirstExistingWhenDestinationMissing(
            destination,
            new[] { Path.Combine(tempRoot, "missing.cfg") });

        Assert.False(copied);
        Assert.False(File.Exists(destination));
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private string WriteFile(string fileName, string content)
    {
        string path = Path.Combine(tempRoot, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
