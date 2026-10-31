using FatFish.Core.Host;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 窗口位置记忆。
///
/// ★ 这一组测试的核心只有一个场景：**用户换了显示器 / 拔掉了外接屏**。
///   存下来的坐标是 (2400, 200)，而当前只有一块 1920×1080 的屏 ——
///   窗口「存在」，但在屏幕外面。表现是：任务栏有图标、点不动、像死了一样。
///
///   只存不验的实现会把这个故障永久保留下来，而且每次换显示器都会重现一次。
/// </summary>
public class WindowStateTests
{
    private static readonly ScreenRect Primary = new(0, 0, 1920, 1040);

    // ------------------------------------------------------------------
    // 可见性修正
    // ------------------------------------------------------------------

    [Fact]
    public void Clamp_BringsOffscreenWindowBackToPrimary()
    {
        // 外接屏上的位置：x=2400 完全在主屏右侧之外
        var stored = new WindowPlacement(2400, 200, 1200, 800);

        var fixedState = WindowPlacementStore.Clamp(stored, [Primary], Primary);

        // 必须在主屏范围内，且标题栏可见
        Assert.InRange(fixedState.Left, Primary.X - 100, Primary.Right - 160);
        Assert.InRange(fixedState.Top, Primary.Y, Primary.Bottom - 60);

        // 尺寸不该被改动（只挪位置）
        Assert.Equal(1200, fixedState.Width);
        Assert.Equal(800, fixedState.Height);
    }

    [Fact]
    public void Clamp_KeepsWindowOnSecondaryScreenWhenItStillExists()
    {
        var second = new ScreenRect(1920, 0, 1920, 1040);
        var stored = new WindowPlacement(2200, 150, 1200, 800);

        var fixedState = WindowPlacementStore.Clamp(stored, [Primary, second], Primary);

        // 副屏还在 → 位置原样保留（不多此一举地搬回主屏）
        Assert.Equal(2200, fixedState.Left);
        Assert.Equal(150, fixedState.Top);
    }

    [Fact]
    public void Clamp_ShrinksOversizedWindow()
    {
        var stored = new WindowPlacement(0, 0, 5000, 3000);

        var fixedState = WindowPlacementStore.Clamp(stored, [Primary], Primary);

        Assert.True(fixedState.Width <= Primary.Width, $"宽度 {fixedState.Width} 超过屏幕 {Primary.Width}");
        Assert.True(fixedState.Height <= Primary.Height);
    }

    [Fact]
    public void Clamp_EnlargesTinyWindow()
    {
        var stored = new WindowPlacement(100, 100, 200, 150);

        var fixedState = WindowPlacementStore.Clamp(stored, [Primary], Primary);

        Assert.Equal(WindowPlacement.MinWidth, fixedState.Width);
        Assert.Equal(WindowPlacement.MinHeight, fixedState.Height);
    }

    [Fact]
    public void Clamp_HandlesNaN()
    {
        // 手改过的 JSON、或者某个 DPI 计算出了 0/0 —— 都会得到 NaN。
        // NaN 会污染所有后续比较（NaN < x 恒为 false），所以必须在入口就挡掉。
        var stored = new WindowPlacement(double.NaN, double.NaN, double.NaN, double.NaN);

        var fixedState = WindowPlacementStore.Clamp(stored, [Primary], Primary);

        Assert.False(double.IsNaN(fixedState.Left));
        Assert.False(double.IsNaN(fixedState.Width));
        Assert.InRange(fixedState.Width, WindowPlacement.MinWidth, Primary.Width);
    }

    [Fact]
    public void Clamp_NoScreensFallsBackToCentered()
    {
        var stored = new WindowPlacement(2400, 200, 1200, 800);

        var fixedState = WindowPlacementStore.Clamp(stored, Array.Empty<ScreenRect>(), Primary);

        Assert.Equal(Primary.Width / 2, fixedState.Left + fixedState.Width / 2, precision: 0);
    }

    [Fact]
    public void Clamp_KeepsMaximizedFlag()
    {
        var stored = new WindowPlacement(2400, 200, 1200, 800, Maximized: true);

        var fixedState = WindowPlacementStore.Clamp(stored, [Primary], Primary);

        Assert.True(fixedState.Maximized);
    }

    [Fact]
    public void Clamp_PartiallyVisibleWindowIsNudgedNotRecentered()
    {
        // 只露出右下角一点点：标题栏抓不住，但用户能看见它 →
        // 应该「推回来」而不是「搬家到屏幕中间」（后者对用户是意外跳动）。
        var stored = new WindowPlacement(1850, 1000, 1200, 800);

        var fixedState = WindowPlacementStore.Clamp(stored, [Primary], Primary);

        Assert.True(fixedState.Left >= Primary.X - 100);
        Assert.True(fixedState.Left < Primary.Right);
        Assert.True(fixedState.Top <= Primary.Bottom - 60);
    }

    // ------------------------------------------------------------------
    // 存取
    // ------------------------------------------------------------------

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var root = TempDir();

        try
        {
            var saved = new WindowPlacement(300, 200, 1100, 750);
            WindowPlacementStore.Save(root, saved);

            var loaded = WindowPlacementStore.Load(root, [Primary], Primary);

            Assert.Equal(saved, loaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFileFallsBackToCentered()
    {
        var root = TempDir();

        try
        {
            var loaded = WindowPlacementStore.Load(root, [Primary], Primary);

            Assert.Equal(WindowPlacement.DefaultWidth, loaded.Width);
            Assert.False(loaded.Maximized);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_CorruptFileFallsBackToCenteredWithoutThrowing()
    {
        var root = TempDir();

        try
        {
            var path = WindowPlacementStore.PathFor(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");

            var loaded = WindowPlacementStore.Load(root, [Primary], Primary);

            Assert.Equal(WindowPlacement.DefaultWidth, loaded.Width);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_OverwritesPreviousState()
    {
        var root = TempDir();

        try
        {
            WindowPlacementStore.Save(root, new WindowPlacement(10, 10, 900, 600));
            WindowPlacementStore.Save(root, new WindowPlacement(20, 20, 950, 650));

            var loaded = WindowPlacementStore.Load(root, [Primary], Primary);

            Assert.Equal(950, loaded.Width);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fatfish_win_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
