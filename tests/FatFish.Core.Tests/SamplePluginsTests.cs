using FatFish.Core.Plugins;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 仓库自带的示例 / 生产插件必须能装载。
/// </summary>
/// <remarks>
/// 为什么值得为「几个 JSON 文件」写测试：
/// 插件清单的校验规则很严（命名约定、能力声明、脚本必须存在、风险推断……），
/// 而**规则只会收紧、不会自己放宽**。今天能装的清单，改完解析器之后可能就装不上了 ——
/// 而这种回归不会让任何代码变红，只会在用户那边表现为「插件列表突然空了」。
///
/// 所以这里从仓库的 <c>plugins/</c> 目录真读一遍，任何诊断都算失败。
/// 顺带把 gpt-image-2 生图插件的契约钉住：工具名、必填参数、风险档、脚本存在。
/// </remarks>
public class SamplePluginsTests
{
    /// <summary>
    /// 从测试程序集目录向上找仓库里的 <c>plugins/</c>。
    /// </summary>
    /// <remarks>
    /// 不用「相对路径拼五个 ..」：测试可能在发布目录、也可能换了层级跑。
    /// 向上找目录名这种写法对位置不敏感，找不到时下面的用例会安静跳过
    /// （在纯发布包里没有源码目录，跳过比误报更合理）。
    /// </remarks>
    private static string? FindPluginsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "plugins");

            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "liqiu-image", "plugin.json")))
            {
                return candidate;
            }
        }

        return null;
    }

    [Fact]
    public void AllRepoPlugins_ParseWithoutAnyDiagnosticError()
    {
        var root = FindPluginsDirectory();

        if (root is null)
            return; // 没有源码目录（例如发布包里跑测试），跳过

        var manifests = Directory
            .EnumerateFiles(root, PluginManifestParser.ManifestFileName, SearchOption.AllDirectories)
            .ToList();

        Assert.NotEmpty(manifests);

        foreach (var path in manifests)
        {
            var outcome = PluginManifestParser.ParseFile(path);

            var errors = outcome.Diagnostics
                .Where(d => d.Level == PluginDiagnosticLevel.Error)
                .Select(d => d.ToString())
                .ToList();

            Assert.True(outcome.Ok && errors.Count == 0,
                Path.GetFileName(Path.GetDirectoryName(path)!) + " 装载失败：\n" + string.Join("\n", errors));
        }
    }

    [Fact]
    public void LiqiuImage_DeclaresExpectedContract()
    {
        var root = FindPluginsDirectory();

        if (root is null)
            return;

        var outcome = PluginManifestParser.ParseFile(Path.Combine(root, "liqiu-image", "plugin.json"));

        Assert.True(outcome.Ok);
        Assert.False(outcome.HasError);

        var manifest = outcome.Manifest!;

        Assert.Equal("liqiu-image", manifest.Id);

        // 本机装的 liqiu-image 可能有多个工具（作者自用版）；只验 image_generate 这一条契约。
        var tool = manifest.Tools.Single(t => t.Name == "image_generate");

        Assert.Equal("image_generate", tool.Name);

        // 模型完全靠 description 决定何时调用，所以它不能是空的敷衍话
        Assert.True(tool.Description.Length > 80);

        // ---- 参数契约 ----
        Assert.Contains("prompt", tool.ParameterNames);
        Assert.Equal("prompt", Assert.Single(tool.RequiredParameters));

        // size / quality 必须是枚举，否则模型会自由发挥出不存在的尺寸
        var json = tool.Parameters.GetRawText();
        Assert.Contains("\"enum\"", json);
        Assert.Contains("4096x4096", json);

        // ---- 风险档：python 执行面 ⇒ 高，且必须有副作用 ----
        Assert.Equal(PluginRunKind.Python, tool.Run.Kind);
        Assert.Equal(PluginRiskTier.High, tool.EffectiveRisk);
        Assert.False(tool.IsReadOnly);
        Assert.True(tool.HasSideEffects);

        // ---- 脚本必须真的在 ----
        Assert.True(File.Exists(tool.Run.Python!.ScriptPath),
            "清单指向的脚本不存在：" + tool.Run.Python.ScriptPath);
    }

    [Fact]
    public void LiqiuImage_ScriptNeverHardcodesASecret()
    {
        var root = FindPluginsDirectory();

        if (root is null)
            return;

        var script = Path.Combine(root, "liqiu-image", "draw.py");
        var code = File.ReadAllText(script);

        // 密钥只能从环境变量来。硬编码进脚本等于「备份/截图/发群里」都在泄露它。
        Assert.Contains("LIQIU_API_KEY", code);
        Assert.DoesNotContain("Bearer sk-", code, StringComparison.OrdinalIgnoreCase);

        // 落盘位置必须受工作台约束（脚本里那句判断丢了的话，插件就能往任意路径写）
        Assert.Contains("跑出了工作台", code);

        // 参数只从 FATFISH_ARG_* 读，不从命令行读
        Assert.Contains("FATFISH_ARG_", code);
        Assert.DoesNotContain("sys.argv[1]", code);
    }
}
