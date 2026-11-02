using NockMon.Core.Native;

namespace NockMon.Core.Tests;

public class UserAccessTests
{
    [Fact]
    public void CanListFolder_AsTheUserWithoutElevation()
    {
        Assert.True(UserAccess.CanListFolder(Path.GetTempPath()));
        Assert.True(UserAccess.CanListFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        // Administrators and SYSTEM only: refused even when this process is elevated (it answers for the user's other
        // programs). Not where UAC is off (build machines): the user's programs are administrators there.
        if (!Environment.IsPrivilegedProcess || UserAccess.HasLinkedToken())
            Assert.False(UserAccess.CanListFolder(Path.Combine(Environment.SystemDirectory, "config")));
        Assert.False(UserAccess.CanListFolder(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }
}
