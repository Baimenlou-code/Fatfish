using System.Globalization;

namespace FatFish.App;

/// <summary>
/// 冒烟测试用的小工具（与 <c>App.xaml.cs</c> 同一个 partial 类）。
/// </summary>
/// <remarks>
/// <b>这个文件是新建的，此前不存在</b>（全仓库搜 "App.Smoke" 命中 0；<c>partial class App</c>
/// 只有 <c>App.xaml.cs</c> 一处，那里原有的滚动条 / 换肤 / 审批栏断言一个字都没动）。
///
/// <b>为什么单独开文件，而不是塞进 <c>App.xaml.cs</c>：</b>
/// 「加宽 .env 编辑栏」这件事必须先量出**现在有多宽**，否则只能靠猜。
/// 量宽度的语句已经加进 <c>RunEnvEditorSmoke</c>，它用到两个纯工具方法
/// （<see cref="SmokeNumber"/> 读可配置的窗口宽度、<see cref="Px"/> 把像素打成整数文本）。
/// 把这两个方法放在独立文件里，是为了让 <c>App.xaml.cs</c> 那几条主线
/// （换肤底稿、异常兜底、冒烟分发）不被这类一次性工具淹掉，
/// 也避免为了两行 helper 再整份重写那个 760 行的启动文件。
/// </remarks>
public partial class App
{
    /// <summary>从环境变量读一个数值，缺失或写错就用默认值。</summary>
    /// <remarks>
    /// 用 <see cref="CultureInfo.InvariantCulture"/> 解析：冒烟脚本可能在任何区域设置下跑，
    /// 而「把 1.5 读成 15」这类错误在界面尺寸上不会报错，只会得出一个奇怪的比例。
    /// </remarks>
    private static double SmokeNumber(string name, double fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    /// <summary>逻辑像素 → 不带小数的字符串（不变文化，满足 CA1305）。</summary>
    private static string Px(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}
