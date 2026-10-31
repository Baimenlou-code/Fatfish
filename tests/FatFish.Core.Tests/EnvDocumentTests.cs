using FatFish.Core.Config;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// .env 文档模型测试 —— 重点只有一个：<b>读进来再写回去，没改的东西一个字节都不许变</b>。
/// </summary>
/// <remarks>
/// 为什么这一条值得写这么多用例：编辑器一旦做不到「没改就不动」，
/// 用户第一次保存就会丢掉自己的注释、分组空行、<c>export</c> 前缀和引号风格。
/// 而这件事**当场没有任何提示** —— 等用户某天想找回那条「这个 key 是 8 月谁给的」时才发现。
/// 所以「往返一致」必须由测试守住，不能靠 code review。
/// </remarks>
public class EnvDocumentTests : IDisposable
{
    private readonly string _dir;

    public EnvDocumentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fatfish_doc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论
        }

        GC.SuppressFinalize(this);
    }

    private const string Sample = """
# ===== 主模型 =====
# 这个 key 是 8 月换的，旧的在下面注释里留着
FATFISH_API_KEY=sk-abc123

FATFISH_MODEL = deepseek-flash
export LEGACY_KEY="v a l"
SINGLE='x y'
HASHKEY=a#b
EMPTY=
THIS LINE IS BROKEN
   # 缩进注释
""";

    [Fact]
    public void RoundTrip_Unchanged_IsByteIdentical()
    {
        var doc = EnvDocument.Parse(Sample);

        Assert.Equal(Sample, doc.Serialize());
    }

    [Fact]
    public void RoundTrip_LfFileStaysLf()
    {
        var text = "# 注释\nA=1\nB=2\n";

        var doc = EnvDocument.Parse(text);

        Assert.Equal("\n", doc.NewLine);
        Assert.Equal(text, doc.Serialize());
    }

    [Fact]
    public void RoundTrip_NoTrailingNewline_IsPreserved()
    {
        var text = "A=1\r\nB=2";

        var doc = EnvDocument.Parse(text);

        Assert.False(doc.EndsWithNewLine);
        Assert.Equal(text, doc.Serialize());
    }

    [Fact]
    public void RoundTrip_EmptyFile_IsEmpty()
    {
        Assert.Equal(string.Empty, EnvDocument.Parse("").Serialize());
        Assert.Equal(string.Empty, EnvDocument.Parse(null).Serialize());
    }

    [Fact]
    public void Parse_ClassifiesLines()
    {
        var doc = EnvDocument.Parse(Sample);

        Assert.Contains(doc.Lines, l => l.Kind == EnvLineKind.Comment && l.Raw.Contains("主模型"));
        Assert.Contains(doc.Lines, l => l.Kind == EnvLineKind.Blank);
        Assert.Contains(doc.Lines, l => l.Kind == EnvLineKind.Invalid && l.Raw.Contains("BROKEN"));

        // export 前缀被识别，但写回时仍然保留
        var legacy = doc.Lines.Single(l => l.Key == "LEGACY_KEY");
        Assert.True(legacy.Exported);
        Assert.Equal("v a l", legacy.Value);
    }

    [Fact]
    public void Get_DecodesQuotesAndIsCaseInsensitive()
    {
        var doc = EnvDocument.Parse(Sample);

        Assert.Equal("sk-abc123", doc.Get("FATFISH_API_KEY"));
        Assert.Equal("sk-abc123", doc.Get("fatfish_api_key"));
        Assert.Equal("v a l", doc.Get("LEGACY_KEY"));
        Assert.Equal("x y", doc.Get("SINGLE"));
        Assert.Equal("a#b", doc.Get("HASHKEY")); // 行尾 # 不当注释剥离
        Assert.Equal(string.Empty, doc.Get("EMPTY"));
        Assert.Null(doc.Get("NOT_THERE"));
    }

    [Fact]
    public void Set_ExistingKey_UpdatesInPlaceAndKeepsEverythingElse()
    {
        var doc = EnvDocument.Parse(Sample);

        doc.Set("FATFISH_MODEL", "deepseek-v4-pro");

        var after = doc.Serialize();

        Assert.Contains("FATFISH_MODEL=deepseek-v4-pro", after);
        Assert.DoesNotContain("deepseek-flash", after);

        // 位置没变：改的这一行仍然夹在空行和下一行之间
        // ★ 这里要先把 CRLF 归一化再切：Sample 是 raw string，
        //   它继承的是**源文件**的换行符（本仓库 .cs 是 CRLF），
        //   直接 Split("\n") 会得到一堆以 "\r" 结尾的行。
        var lines = after.Replace("\r\n", "\n").Split("\n");
        Assert.Equal("", lines[3]);
        Assert.Equal("FATFISH_MODEL=deepseek-v4-pro", lines[4]);
        Assert.Equal("export LEGACY_KEY=\"v a l\"", lines[5]);

        // 注释、坏行、空行一个都没少
        Assert.Contains("# ===== 主模型 =====", after);
        Assert.Contains("THIS LINE IS BROKEN", after);
        Assert.Contains("# 缩进注释", after);
    }

    [Fact]
    public void Set_NewKey_AppendedAtEnd()
    {
        var doc = EnvDocument.Parse("A=1\n");

        doc.Set("NEW_KEY", "x");

        Assert.Equal("A=1\nNEW_KEY=x\n", doc.Serialize());
        Assert.Equal("x", doc.Get("NEW_KEY"));
    }

    [Fact]
    public void Set_NewKey_OnFileWithoutTrailingNewline_StaysSeparated()
    {
        var doc = EnvDocument.Parse("A=1");

        doc.Set("B", "2");

        Assert.Equal("A=1\r\nB=2", doc.Serialize());
    }

    [Fact]
    public void Set_ValueNeedingQuotes_IsQuotedAndReadsBack()
    {
        var doc = EnvDocument.Parse("A=1\n");

        doc.Set("PADDED", "  x  ");
        doc.Set("QUOTED", "he said \"hi\"");
        doc.Set("BACKSLASH", @"C:\tmp\a");

        var round = EnvDocument.Parse(doc.Serialize());

        Assert.Equal("  x  ", round.Get("PADDED"));
        Assert.Equal("he said \"hi\"", round.Get("QUOTED"));
        Assert.Equal(@"C:\tmp\a", round.Get("BACKSLASH"));
    }

    [Fact]
    public void Set_RejectsNewlineAndInvalidKey()
    {
        var doc = EnvDocument.Parse("A=1\n");

        Assert.Throws<ArgumentException>(() => doc.Set("B", "line1\nline2"));
        Assert.Throws<ArgumentException>(() => doc.Set("BAD KEY", "x"));
        Assert.Throws<ArgumentException>(() => doc.Set("1LEADING", "x"));
    }

    [Fact]
    public void Remove_DeletesAllOccurrences()
    {
        var doc = EnvDocument.Parse("A=1\nB=2\nA=3\n");

        Assert.Equal(2, doc.Remove("a"));
        Assert.Equal("B=2\n", doc.Serialize());
    }

    [Fact]
    public void DuplicateKeys_AreReported_AndFirstWins()
    {
        var doc = EnvDocument.Parse("A=first\nB=1\nA=second\n");

        Assert.Equal("A", string.Join(",", doc.DuplicateKeys));
        Assert.Equal("first", doc.Get("A")); // 与 EnvFile 的实际生效顺序一致
    }

    [Fact]
    public void Save_WritesFile_AndKeepsOneBackupPerSave()
    {
        var path = Path.Combine(_dir, ".env");
        File.WriteAllText(path, "A=1\n");

        var doc = EnvDocument.Load(path);
        doc.Set("A", "2");

        var backup1 = doc.Save(path);

        Assert.NotNull(backup1);
        Assert.True(File.Exists(backup1));
        Assert.Equal("A=1\n", File.ReadAllText(backup1!));
        Assert.Equal("A=2\n", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp")); // 临时文件不能留在磁盘上

        // 同一秒内再存一次：不能覆盖上一份备份
        doc.Set("A", "3");
        var backup2 = doc.Save(path);

        Assert.NotNull(backup2);
        Assert.NotEqual(backup1, backup2);
        Assert.True(File.Exists(backup1));
    }

    [Fact]
    public void Save_PreservesBom()
    {
        var path = Path.Combine(_dir, ".env");

        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes("A=1\n"))
            .ToArray();

        File.WriteAllBytes(path, withBom);

        var doc = EnvDocument.Load(path);

        Assert.True(doc.HasBom);

        doc.Set("A", "2");
        doc.Save(path);

        var bytes = File.ReadAllBytes(path);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("A=2\n", System.Text.Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyDocument()
    {
        var doc = EnvDocument.Load(Path.Combine(_dir, "nope.env"));

        Assert.Empty(doc.Lines);
        Assert.False(doc.Contains("A"));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("with space inside")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("\"starts with quote")]
    [InlineData("has \"inner\" quotes")]
    [InlineData(@"back\slash")]
    [InlineData(@"end\\")]
    [InlineData(@"\""quote\""after\\slash")]
    [InlineData("a#b")]
    [InlineData("emoji 🐟 中文")]
    public void Codec_EncodeThenDecode_IsIdentity(string value)
    {
        var encoded = EnvValueCodec.Encode(value);

        Assert.Equal(value, EnvValueCodec.Decode(encoded));

        // 编码结果不能含换行，否则整个文件结构就变了
        Assert.DoesNotContain('\n', encoded);
        Assert.DoesNotContain('\r', encoded);
    }

    [Fact]
    public void Codec_DoesNotQuoteOrdinaryValues()
    {
        Assert.Equal("sk-abc123", EnvValueCodec.Encode("sk-abc123"));
        Assert.Equal(string.Empty, EnvValueCodec.Encode(string.Empty));
        Assert.True(EnvValueCodec.NeedsQuoting(" x"));
        Assert.False(EnvValueCodec.NeedsQuoting("x y")); // 中间空格无妨，只有首尾会被 Trim
    }
}
