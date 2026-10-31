namespace FatFish.Core.Net;

/// <summary>
/// 一个搜索后端。
///
/// 与 Python 版 <c>SearchProvider</c> 一一对应，但去掉了 Python 里的
/// 「自动记账钩子」（<c>_auto_record</c>）：C# 版把记账放在
/// <see cref="NetService"/> 这一层做，provider 只管发请求。
/// 理由：provider 有 6 个、记账只有一种规矩；放在 provider 里就是 6 份各自为政的实现，
/// 迟早出现「某个后端漏记一次」而这种漏洞在账单上根本看不出来。
/// </summary>
public interface INetProvider
{
    /// <summary>后端标识（deepseek / tavily）。</summary>
    string Name { get; }

    /// <summary>是否支持抓正文。</summary>
    bool SupportsFetch { get; }

    /// <summary>配置是否齐全（缺 key 就是 false）。</summary>
    bool IsAvailable { get; }

    /// <summary>搜索。失败抛 <see cref="NetException"/>；「搜到 0 条」不算失败，返回空列表。</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(NetQuery query, CancellationToken ct = default);

    /// <summary>抓正文。失败页放在 <see cref="FetchHit.Error"/> 里，不整体抛异常。</summary>
    Task<IReadOnlyList<FetchHit>> FetchAsync(
        IReadOnlyList<string> urls, int maxChars, CancellationToken ct = default);
}
