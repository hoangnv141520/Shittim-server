using Microsoft.Extensions.Logging.Abstractions;
using Shittim_Server.Services;
using Xunit;

namespace Shittim_Server.Tests;

public class OfficialGrap64RestoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"shittim-grap-restore-{Guid.NewGuid():N}");
    private readonly string? oldPath = Environment.GetEnvironmentVariable("SHITTIM_CLIENT_GRAP64_PATH");
    private readonly string? oldStub = Environment.GetEnvironmentVariable("SHITTIM_CLIENT_GRAP64_STUB_PATH");
    private string Target => Path.Combine(dir, "grap64.dll");

    public OfficialGrap64RestoreTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "stub.dll"), [1, 2, 3]);
        Environment.SetEnvironmentVariable("SHITTIM_CLIENT_GRAP64_PATH", Target);
        Environment.SetEnvironmentVariable("SHITTIM_CLIENT_GRAP64_STUB_PATH", Path.Combine(dir, "stub.dll"));
    }

    [Fact]
    public void RestoresTheOriginalAndKeepsItsBackupForAnotherSwitch()
    {
        File.WriteAllBytes(Target, [1, 2, 3]);
        File.WriteAllBytes(Target + ".bak", [4, 5, 6]);
        Restore();
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Target));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Target + ".bak"));
        Restore();
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Target));
    }

    [Fact]
    public void MissingOriginalBackupCannotBeReportedAsAnOfficialClient()
    {
        File.WriteAllBytes(Target, [1, 2, 3]);
        Assert.Throws<IOException>(Restore);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Target));
    }

    [Fact]
    public void AClientUpdateCannotBeOverwrittenByAnOlderBackup()
    {
        File.WriteAllBytes(Target, [7, 8, 9]);
        File.WriteAllBytes(Target + ".bak", [4, 5, 6]);
        Assert.Throws<IOException>(Restore);
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Target));
    }

    [Fact]
    public void AStubMislabelledAsTheOriginalIsRejected()
    {
        File.WriteAllBytes(Target, [1, 2, 3]);
        File.WriteAllBytes(Target + ".bak", [1, 2, 3]);
        Assert.Throws<IOException>(Restore);
    }

    private void Restore() => new ClientGrap64ManagementService(NullLogger<ClientGrap64ManagementService>.Instance).RestoreForOfficial();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SHITTIM_CLIENT_GRAP64_PATH", oldPath);
        Environment.SetEnvironmentVariable("SHITTIM_CLIENT_GRAP64_STUB_PATH", oldStub);
        Directory.Delete(dir, true);
    }
}
