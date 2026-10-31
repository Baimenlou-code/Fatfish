using System.Text.Json;
using FatFish.Core.Llm;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// ChatMessage 序列化测试。
///
/// 这些断言看着琐碎，但都是真实踩过的坑：
///   · arguments 若被序列化成对象（而不是字符串），API 会 400；
///   · assistant 无文字时必须发 content:null，发 "" 某些服务端会拒绝；
///   · tool 消息缺 tool_call_id 会直接报错。
/// </summary>
public class ChatMessageTests
{
    [Fact]
    public void System_SerializesRoleAndContent()
    {
        var node = ChatMessage.System("你是肥鱼").ToJson();

        Assert.Equal("system", node["role"]!.GetValue<string>());
        Assert.Equal("你是肥鱼", node["content"]!.GetValue<string>());
    }

    [Fact]
    public void Assistant_WithoutText_EmitsNullContent()
    {
        var msg = ChatMessage.Assistant(null, new[]
        {
            new ToolCall { Id = "call_1", Name = "ws_read", Arguments = "{\"path\":\"a.txt\"}" },
        });

        // ★ 直接检查序列化结果：content 字段必须**存在**且值为 null。
        //   不能用 node["content"] == null 判断 —— JsonNode 在「值就是 null」和
        //   「键不存在」两种情况下都返回 null，无法区分。
        var json = msg.ToJson().ToJsonString();

        Assert.Contains("\"role\":\"assistant\"", json);
        Assert.Contains("\"content\":null", json);
    }

    [Fact]
    public void Assistant_ToolCalls_ArgumentsStayString()
    {
        const string argsJson = "{\"path\":\"src/main.cs\"}";

        var msg = ChatMessage.Assistant(null, new[]
        {
            new ToolCall { Id = "call_abc", Name = "ws_read", Arguments = argsJson },
        });

        var node = msg.ToJson();
        var calls = node["tool_calls"]!.AsArray();

        Assert.Single(calls);
        Assert.Equal("call_abc", calls[0]!["id"]!.GetValue<string>());
        Assert.Equal("function", calls[0]!["type"]!.GetValue<string>());

        // ★ 关键：arguments 是字符串，不是嵌套对象
        var argNode = calls[0]!["function"]!["arguments"]!;
        Assert.Equal(JsonValueKind.String, argNode.GetValueKind());
        Assert.Equal(argsJson, argNode.GetValue<string>());
    }

    [Fact]
    public void ToolResult_CarriesToolCallId()
    {
        var node = ChatMessage.ToolResult("call_abc", "{\"ok\":true}").ToJson();

        Assert.Equal("tool", node["role"]!.GetValue<string>());
        Assert.Equal("call_abc", node["tool_call_id"]!.GetValue<string>());
        Assert.Equal("{\"ok\":true}", node["content"]!.GetValue<string>());
    }

    [Fact]
    public void ParseArguments_ValidJson_ReturnsObject()
    {
        var call = new ToolCall
        {
            Id = "c1",
            Name = "ws_read",
            Arguments = "{\"path\":\"a.txt\",\"n\":3}",
        };

        var args = call.ParseArguments();

        Assert.Equal(JsonValueKind.Object, args.ValueKind);
        Assert.Equal("a.txt", args.GetProperty("path").GetString());
        Assert.Equal(3, args.GetProperty("n").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{broken")]
    [InlineData("not json at all")]
    public void ParseArguments_InvalidOrEmpty_ReturnsEmptyObject(string raw)
    {
        var call = new ToolCall { Id = "c1", Name = "x", Arguments = raw };

        var args = call.ParseArguments();

        // 不抛异常 —— 让工具层报「缺少参数」，错误信息更可读
        Assert.Equal(JsonValueKind.Object, args.ValueKind);
        Assert.Empty(args.EnumerateObject());
    }

    [Fact]
    public void ToAssistantMessage_DropsEmptyText()
    {
        var turn = new LlmTurn
        {
            Text = string.Empty,
            ToolCalls = new[] { new ToolCall { Id = "c1", Name = "ws_read" } },
        };

        var msg = turn.ToAssistantMessage();

        Assert.Null(msg.Content);
        Assert.NotNull(msg.ToolCalls);
        Assert.Single(msg.ToolCalls!);
    }

    [Fact]
    public void ToAssistantMessage_KeepsText()
    {
        var turn = new LlmTurn { Text = "你好", ToolCalls = Array.Empty<ToolCall>() };

        var msg = turn.ToAssistantMessage();

        Assert.Equal("你好", msg.Content);
        Assert.Null(msg.ToolCalls);
    }

    [Fact]
    public void EstimateTokens_GrowsWithContent()
    {
        var small = ChatMessage.User("hi").EstimateTokens();
        var big = ChatMessage.User(new string('x', 4000)).EstimateTokens();

        Assert.True(big > small * 10);
    }
}
