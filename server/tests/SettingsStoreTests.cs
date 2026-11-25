using TaoOps.Server.Settings;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>系统设置存储（backlog F4）：默认值、落盘回读、原子写盘残留清理。</summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"taoops-settings-{Guid.NewGuid():N}");
        _store = new SettingsStore(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_ReturnsDefaults_WhenFileMissing()
    {
        var s = _store.Load();
        Assert.Equal(90, s.DiskCriticalPct);
        Assert.Equal(85, s.DiskResetPct);
    }

    [Fact]
    public void Save_PersistsAndRoundTrips()
    {
        _store.Save(new AlertThresholdSettings { DiskCriticalPct = 95, DiskResetPct = 80 });
        Assert.True(File.Exists(_store.FilePath));
        var loaded = _store.Load();
        Assert.Equal(95, loaded.DiskCriticalPct);
        Assert.Equal(80, loaded.DiskResetPct);
    }

    [Fact]
    public void Save_RemovesTmpFile()
    {
        _store.Save(new AlertThresholdSettings());
        Assert.False(File.Exists(_store.FilePath + ".tmp"));
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileCorrupt()
    {
        File.WriteAllText(_store.FilePath, "{ not json");
        var s = _store.Load();
        Assert.Equal(90, s.DiskCriticalPct);
        Assert.Equal(85, s.DiskResetPct);
    }
}
