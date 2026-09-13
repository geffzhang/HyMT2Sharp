using System.Text.Json;
using Sdcb.HyMT2Sharp.Server;

namespace Sdcb.HyMT2Sharp.Tests;

public sealed class ChatCompletionMessageTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web);

    [Fact]
    public void GetText_ReadsStringContent()
    {
        ChatCompletionMessageDto dto = JsonSerializer.Deserialize<ChatCompletionMessageDto>(
            """{"role":"user","content":"hello"}""", Json)!;
        Assert.Equal("user", dto.Role);
        Assert.Equal("hello", dto.GetText());
    }

    [Fact]
    public void GetText_ConcatenatesContentParts()
    {
        ChatCompletionMessageDto dto = JsonSerializer.Deserialize<ChatCompletionMessageDto>(
            """{"role":"user","content":[{"type":"text","text":"Hello"},{"type":"text","text":" world"}]}""", Json)!;
        Assert.Equal("Hello world", dto.GetText());
    }
}
