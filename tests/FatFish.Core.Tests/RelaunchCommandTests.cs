using FatFish.Core.Host;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 重启命令重建测试。
/// </summary>
/// <remarks>
/// 这块逻辑很小，但写错的表现是「点了重启没反应」—— 一个既没有异常、
/// 也没有日志、还没有任何线索的症状。所以三种启动方式各写一条。
///
/// ★ 关于断言写法：参数列表一律拼成字符串再比，不用 <c>Assert.Equal(string[], IReadOnlyList&lt;string&gt;)</c>。
///   原因有两个：
///     · 分析器会为「传给方法的常量数组字面量」报 CA1861（本仓库要求 0 警告）；
///     · <c>Assert.Equal</c> 有十几个重载，数组/集合/可枚举三套混在一起时，
///       选到哪个重载并不直观 —— 拼成字符串反而是最没有歧义的写法。
/// </remarks>
public class RelaunchCommandTests
{
    private static string Args(RelaunchPlan plan) => string.Join("|", plan.Arguments);

    [Fact]
    public void DotnetHost_KeepsDllAsFirstArgument()
    {
        var plan = RelaunchCommand.Build(
            @"C:\Program Files\dotnet\dotnet.exe",
            new List<string> { @"E:\app\FatFish.dll", "--workspace", @"E:\my ws" });

        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", plan.FileName);
        Assert.Equal(@"E:\app\FatFish.dll|--workspace|E:\my ws", Args(plan));

        // 带空格的路径必须被引号包起来，否则会被拆成两个参数
        Assert.Contains("\"E:\\my ws\"", plan.ToCommandLine());
    }

    [Fact]
    public void AppHost_DropsTheExeFromArguments()
    {
        var plan = RelaunchCommand.Build(
            @"E:\app\FatFish.exe",
            new List<string> { @"E:\app\FatFish.exe", "--workspace", "x" });

        Assert.Equal(@"E:\app\FatFish.exe", plan.FileName);
        Assert.Equal("--workspace|x", Args(plan));
    }

    [Fact]
    public void AppHost_WithoutExtraArguments_ProducesEmptyCommandLine()
    {
        var plan = RelaunchCommand.Build(
            @"E:\app\FatFish.exe",
            new List<string> { @"E:\app\FatFish.exe" });

        Assert.Empty(plan.Arguments);
        Assert.Equal(@"E:\app\FatFish.exe", plan.Describe());
    }

    [Fact]
    public void MissingProcessPath_FallsBackToEntryOrDotnet()
    {
        // 托管入口且没给宿主路径：只能假设宿主是 PATH 里的 dotnet。
        // （不是 FatFish —— 那个字符串从来不是可执行文件。）
        Assert.Equal("dotnet", RelaunchCommand.Build(
            null, new List<string> { @"E:\app\FatFish.dll" }).FileName);

        // 原生入口且没给进程路径：argv[0] 本身就是可执行文件
        Assert.Equal(@"E:\app\FatFish.exe", RelaunchCommand.Build(
            null, new List<string> { @"E:\app\FatFish.exe" }).FileName);
    }

    [Fact]
    public void EmptyCommandLine_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            RelaunchCommand.Build("x.exe", new List<string>()));
    }

    [Fact]
    public void EmptyArgument_IsQuoted()
    {
        var plan = RelaunchCommand.Build(
            "app.exe", new List<string> { "app.exe", "" });

        Assert.Equal("\"\"", plan.ToCommandLine());
    }
}
