using System.Runtime.InteropServices;
using System.Text;

namespace FatFish.Core.Tools;

/// <summary>
/// 子进程输出的字节 → 文本解码。
///
/// 为什么需要这个类：中文 Windows 上 <c>cmd</c> 的内建命令（dir / type / ver…）
/// 把文本按**控制台 OEM 代码页**（简中即 936 / GBK）写成字节，
/// 而不是 UTF-8。直接用 UTF-8 读会得到一屏乱码。
///
/// 三条实测结论（写这个类的依据，都是跑出来的，不是查文档猜的）：
///   1. .NET Core 默认**不支持**代码页 936 ——
///      <c>Encoding.GetEncoding(936)</c> 抛 NotSupportedException: "No data is available for encoding 936"。
///      必须先 <c>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)</c>。
///   2. <c>chcp 65001</c> 对重定向输出**无效** ——
///      实测「裸 dir」与「chcp 65001 后 dir」输出的字节完全一致（2846 字节，全 GBK）。
///      所以不能指望把控制台切到 UTF-8 来绕开解码问题。
///   3. 同一个进程里混着两种编码很常见：
///      git / python / node 输出 UTF-8，cmd 内建命令输出 GBK。
///      所以默认策略是「先按严格 UTF-8 试，失败再退 OEM 代码页」。
///
/// 注：<c>CodePagesEncodingProvider</c> 无需额外 NuGet 包 ——
/// 它随 net10.0 的共享框架一起提供（已实测在本项目可编译可运行）。
/// </summary>
public static class ProcessEncoding
{
    /// <summary>严格模式 UTF-8：遇到非法字节**抛异常**，用于「判断这是不是 UTF-8」。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>宽松 UTF-8：非法字节替换成 U+FFFD，用于用户显式指定 utf-8 时。</summary>
    private static readonly UTF8Encoding LenientUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    /// <summary>本机的 OEM 代码页编码（简中 Windows 上为 GBK）。构造时解析一次。</summary>
    public static Encoding Oem { get; }

    /// <summary>本机 OEM 代码页号（简中 936）。</summary>
    public static int OemCodePage { get; }

    static ProcessEncoding()
    {
        // 不注册的话，936 / 437 / 850 这些代码页全都拿不到。
        // 注册是进程级、幂等的，重复调用无害。
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception)
        {
            // 注册失败不致命：下面会自动降级到 Latin1
        }

        (Oem, OemCodePage) = ResolveOem();
    }

    private static (Encoding Encoding, int CodePage) ResolveOem()
    {
        var cp = 0;

        try
        {
            // 只在 Windows 上存在；其它平台抛 DllNotFoundException，被下面吞掉
            cp = GetOEMCP();
        }
        catch (Exception)
        {
            cp = 0;
        }

        foreach (var candidate in new[] { cp, 936, 437 })
        {
            if (candidate <= 0)
                continue;

            try
            {
                return (Encoding.GetEncoding(candidate), candidate);
            }
            catch (Exception)
            {
                // 这个代码页不可用，试下一个
            }
        }

        // 最后的兜底：Latin1 是一字节对一字节，永不抛异常，只会丢中文
        return (Encoding.Latin1, 28591);
    }

    [DllImport("kernel32.dll")]
    private static extern int GetOEMCP();

    /// <summary>
    /// 把子进程输出的原始字节解码成文本。
    /// </summary>
    /// <param name="bytes">原始字节。</param>
    /// <param name="mode">
    /// 解码方式，大小写不敏感：
    ///   auto（默认）= 先严格 UTF-8，失败退 OEM；
    ///   utf-8       = 强制 UTF-8（非法字节替换为 U+FFFD）；
    ///   gbk / oem   = 强制 OEM 代码页。
    /// </param>
    public static string Decode(byte[] bytes, string? mode = null)
        => DecodeDetailed(bytes, mode).Text;

    /// <summary>
    /// 与 <see cref="Decode(byte[], string?)"/> 相同，但把「实际用了哪个编码」一并返回。
    ///
    /// 为什么需要：后台任务的增量读取要按字符数截断，而截断后必须能**精确**算出
    /// 「这段文本对应多少原始字节」，否则下次的 offset 会错位 ——
    /// 错位表现为 tail 时候前面几个字重复出现或整段消失，且只在中文输出时出现。
    /// 回算字节数需要知道你用的是 UTF-8 还是 GBK，所以编码必须一起带出来。
    /// </summary>
    public static (string Text, Encoding Encoding) DecodeDetailed(byte[] bytes, string? mode = null)
    {
        if (bytes is null || bytes.Length == 0)
            return (string.Empty, LenientUtf8);

        // BOM 优先：带 BOM 的 UTF-16 / UTF-8 不必猜
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);

            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
                        Encoding.BigEndianUnicode);
        }

        (string Text, Encoding Encoding) result = (mode ?? "auto").Trim().ToLowerInvariant() switch
        {
            "utf-8" or "utf8" => (LenientUtf8.GetString(bytes), LenientUtf8),
            "gbk" or "gb2312" or "936" => (Encoding.GetEncoding(936).GetString(bytes),
                                           Encoding.GetEncoding(936)),
            "oem" or "console" => (Oem.GetString(bytes), Oem),
            _ => DecodeAuto(bytes),
        };

        // 去掉残留的 UTF-8 BOM（GetString 不会自动剥离）
        var text = result.Text;
        if (text.Length > 0 && text[0] == '\uFEFF')
            result = (text[1..], result.Encoding);

        return result;
    }

    private static (string Text, Encoding Encoding) DecodeAuto(byte[] bytes)
    {
        try
        {
            return (StrictUtf8.GetString(bytes), StrictUtf8);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8 —— 最可能是 cmd 内建命令的 GBK 输出
            return (Oem.GetString(bytes), Oem);
        }
    }

    /// <summary>
    /// 是否是「二进制」内容。
    ///
    /// 判定标准：样本前 N 字节里出现 0x00。文本文件（含 UTF-8 / GBK）不会有裸 NUL，
    /// 而 exe / png / zip / dll 的前几百字节里必然有。简单、快、误判率极低。
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> sample)
        => sample.IndexOf((byte)0) >= 0;
}
