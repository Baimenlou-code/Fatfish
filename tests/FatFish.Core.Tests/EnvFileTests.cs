using FatFish.Core.Config;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// .env 解析测试。
///
/// 目标是与 python-dotenv 的行为对齐 —— 用户已有的 .env 必须能被原样读懂，
/// 不能因为一个奇怪的空格或引号就静默丢掉整行配置。
/// </summary>
/// <remarks>
/// 标注了会改环境变量的测试集合：其中两个用例会临时设置 FATFISH_ENV，
/// 与并行的其它用例隔离，避免它们恰好在错误的时刻读到被改写的值。
/// </remarks>
[Collection(EnvironmentMutatingCollectionDefinition.Name)]
public class EnvFileTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _touched = new();

    public EnvFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fatfish_env_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var key in _touched)
            Environment.SetEnvironmentVariable(key, null);

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响测试结论
        }

        GC.SuppressFinalize(this);
    }

    private string WriteEnv(params string[] lines)
    {
        var path = Path.Combine(_dir, ".env");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>取一个不会被其他测试污染的键名。</summary>
    private string NewKey(string baseName)
    {
        var key = "FFTEST_" + baseName + "_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        _touched.Add(key);
        return key;
    }

    [Fact]
    public void Load_ParsesKeyValuePairs()
    {
        var k1 = NewKey("A");
        var k2 = NewKey("B");

        var path = WriteEnv(
            "# 这是注释",
            "",
            $"{k1}=hello",
            $"{k2} = world with spaces  ");

        var count = EnvFile.Load(path, force: true);

        Assert.Equal(2, count);
        Assert.Equal("hello", Environment.GetEnvironmentVariable(k1));
        Assert.Equal("world with spaces", Environment.GetEnvironmentVariable(k2));
    }

    [Fact]
    public void Load_StripsQuotes()
    {
        var kd = NewKey("DQ");
        var ks = NewKey("SQ");

        var path = WriteEnv(
            $"{kd}=\"quoted value\"",
            $"{ks}='single quoted'");

        EnvFile.Load(path, force: true);

        Assert.Equal("quoted value", Environment.GetEnvironmentVariable(kd));
        Assert.Equal("single quoted", Environment.GetEnvironmentVariable(ks));
    }

    [Fact]
    public void Load_HandlesExportPrefix()
    {
        var key = NewKey("EXP");
        var path = WriteEnv($"export {key}=exported");

        EnvFile.Load(path, force: true);

        Assert.Equal("exported", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void Load_ValueMayContainEquals()
    {
        var key = NewKey("EQ");
        var path = WriteEnv($"{key}=a=b=c");

        EnvFile.Load(path, force: true);

        // 只按第一个 = 切分
        Assert.Equal("a=b=c", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void Load_SkipsMalformedLines()
    {
        var good = NewKey("GOOD");

        var path = WriteEnv(
            "this line has no equals sign",
            "=valueWithNoKey",
            $"  {good}  =  ok  ",
            "123STARTSWITHDIGIT=should be ignored");

        var count = EnvFile.Load(path, force: true);

        Assert.Equal(1, count);
        Assert.Equal("ok", Environment.GetEnvironmentVariable(good));
        Assert.Null(Environment.GetEnvironmentVariable("123STARTSWITHDIGIT"));
    }

    [Fact]
    public void Load_DoesNotOverrideExistingVariable()
    {
        var key = NewKey("NOOVR");

        // 预先设置一个值，模拟系统环境变量优先
        Environment.SetEnvironmentVariable(key, "from-system");

        var path = WriteEnv($"{key}=from-file");
        EnvFile.Load(path, force: true);

        Assert.Equal("from-system", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void Load_MissingFile_ReturnsZero()
    {
        var n = EnvFile.Load(Path.Combine(_dir, "no-such-file.env"), force: true);
        Assert.Equal(0, n);
    }

    [Fact]
    public void LoadNearest_FindsFileInParentDirectory()
    {
        var key = NewKey("NEAR");
        File.WriteAllText(Path.Combine(_dir, ".env"), $"{key}=found");

        var deep = Path.Combine(_dir, "a", "b", "c");
        Directory.CreateDirectory(deep);

        var (path, count) = EnvFile.LoadNearest(deep);

        Assert.NotNull(path);
        Assert.Equal(1, count);
        Assert.Equal("found", Environment.GetEnvironmentVariable(key));
    }

    /// <summary>
    /// FATFISH_ENV 指定了路径，就**只**用它，不再向上搜索。
    ///
    /// 这条守的是一个真实踩到的坑：程序目录下有一份旧 .env，
    /// 而真正要用的配置在工作台根，于是向上搜索总在半路就停下，
    /// 读不到工作台根的那份。两份 .env 的 API Key 不同，结果是
    /// 「从哪个目录启动，就连到哪个账号」，而且一声不响。
    /// </summary>
    [Fact]
    public void LoadNearest_HonorsExplicitEnvPath()
    {
        var key = NewKey("EXPLICIT");

        // 诱饵：子目录里放一份内容不同的 .env
        var decoyDir = Path.Combine(_dir, "decoy");
        Directory.CreateDirectory(decoyDir);
        File.WriteAllText(Path.Combine(decoyDir, ".env"), $"{key}=from-decoy");

        var target = Path.Combine(_dir, "explicit.env");
        File.WriteAllText(target, $"{key}=from-explicit");

        var previous = Environment.GetEnvironmentVariable("FATFISH_ENV");

        try
        {
            Environment.SetEnvironmentVariable("FATFISH_ENV", target);

            var (path, count) = EnvFile.LoadNearest(decoyDir);

            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(path!));
            Assert.Equal(1, count);
            Assert.Equal("from-explicit", Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FATFISH_ENV", previous);
        }
    }

    /// <summary>
    /// 显式指定的路径不存在时，也要把路径原样报出来（Count=0），
    /// 而不是静默回退到自动搜索 —— 后者会让「我明明改了配置」变成永久谜题。
    /// </summary>
    [Fact]
    public void LoadNearest_ExplicitMissingPath_IsReportedNotIgnored()
    {
        var missing = Path.Combine(_dir, "no-such-file.env");
        var previous = Environment.GetEnvironmentVariable("FATFISH_ENV");

        try
        {
            Environment.SetEnvironmentVariable("FATFISH_ENV", missing);

            var (path, count) = EnvFile.LoadNearest(_dir);

            Assert.Equal(missing, path);
            Assert.Equal(0, count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FATFISH_ENV", previous);
        }
    }

    [Fact]
    public void GetBool_UnderstandsCommonForms()
    {
        var key = NewKey("BOOL");

        foreach (var (raw, expected) in new[]
                 {
                     ("1", true), ("true", true), ("YES", true), ("on", true),
                     ("0", false), ("false", false), ("no", false), ("off", false),
                 })
        {
            Environment.SetEnvironmentVariable(key, raw);
            Assert.Equal(expected, EnvFile.GetBool(key, !expected));
        }

        // 无法识别时回落默认值
        Environment.SetEnvironmentVariable(key, "maybe");
        Assert.True(EnvFile.GetBool(key, true));
        Assert.False(EnvFile.GetBool(key, false));
    }
}
