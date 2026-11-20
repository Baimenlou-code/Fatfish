using System.Text;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 测试环境探测。
/// </summary>
/// <remarks>
/// ★ 为什么需要它：接上 GitHub Actions 之后，有 4 条用例在本地全绿、在 CI 全红，
///   而**产品代码一行都没错**：
///
///   1-2. <c>Balance_PicksCnyBalance</c> / <c>Balance_TavilyTakesMinOfPlanAndKey</c>
///        断言 <c>Ok == true</c>，但 <c>BalanceService</c> 在<b>没有 API key</b> 时
///        直接返回 <c>Ok = false</c>（"未找到 TAVILY_API_KEY"）。
///        本地开发机的进程环境里有 key，CI 里没有 —— 于是同一份代码两种结果。
///        这类用例本质测的是「JSON 解析」，不该依赖「本机恰好配了 key」，
///        正确做法是用 <see cref="EnvScope"/> 在用例内临时注入一个假 key。
///
///   3-4. <c>WsRunCmd_PowerShellHandlesQuotesAndChinese</c> / <c>ProcessEncoding_DecodesGbk</c>
///        断言中文能原样穿过 cmd / GBK 编码。英文版 Windows runner 的控制台代码页
///        是 437，<c>GetBytes("中")</c> 只会得到 <c>?</c> —— 这不是缺陷，是环境缺能力。
///        这类用例用 <see cref="CjkFactAttribute"/> 在能力缺失时**显式 Skip**，
///        而不是把断言改弱（改弱就等于把这条守门测试废掉了）。
/// </remarks>
public static class TestEnvironment
{
    /// <summary>当前控制台能否表示中文。</summary>
    public static bool CjkConsoleAvailable { get; } = DetectCjkConsole();

    private static bool DetectCjkConsole()
    {
        try
        {
            // ★ 必须用产品自己的 ProcessEncoding.Oem，不能直接 Encoding.GetEncoding(936)：
            //   .NET Core 默认不认识 936，会用 NotSupportedException 把这里打成 false，
            //   于是在简中机器上也会误判「不支持」，把该跑的用例全 Skip 掉
            //   （第一版就这么写的，本地 987 条里 2 条凭空消失，才发现）。
            //   ProcessEncoding 的静态构造会 RegisterProvider，这里的判断才与线上一致。
            var oem = FatFish.Core.Tools.ProcessEncoding.Oem;
            var bytes = oem.GetBytes("中文");

            // 表示不了时，每个字符都退化成 '?'（如 437）。
            return bytes.Length > 1 && Array.IndexOf(bytes, (byte)'?') < 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // 取不到 OEM 代码页：当作不具备能力，用例显式 Skip。
            return false;
        }
    }
}

/// <summary>
/// 只在「控制台能表示中文」时运行的 <see cref="FactAttribute"/>。
/// 不具备能力时显示为 Skip，而不是失败。
/// </summary>
public sealed class CjkFactAttribute : FactAttribute
{
    public CjkFactAttribute()
    {
        if (!TestEnvironment.CjkConsoleAvailable)
            Skip = "当前控制台代码页无法表示中文（英文版 Windows），该用例只在中文环境有意义";
    }
}

/// <summary>
/// 临时设置环境变量，<see cref="Dispose"/> 时恢复原值（原本不存在就删掉）。
/// </summary>
/// <remarks>
/// 用 <c>using</c> 包住即可，异常路径也会恢复：
/// <code>using var _ = EnvScope.Set("TAVILY_API_KEY", "tvly-test");</code>
/// </remarks>
public sealed class EnvScope : IDisposable
{
    private readonly string _name;
    private readonly string? _original;

    private EnvScope(string name, string value)
    {
        _name = name;
        _original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>设置一个环境变量，返回可恢复的作用域。</summary>
    public static EnvScope Set(string name, string value) => new(name, value);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _original);
        GC.SuppressFinalize(this);
    }
}
