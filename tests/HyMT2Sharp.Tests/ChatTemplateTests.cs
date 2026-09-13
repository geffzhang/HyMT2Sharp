using Sdcb.HyMT2Sharp.Model;

namespace Sdcb.HyMT2Sharp.Tests;

public sealed class ChatTemplateTests
{
    [Fact]
    public void UserTurn_GetsBosAndGenerationPrompt()
    {
        string text = ChatTemplate.RenderHunyuanDense([new ChatMessage("user", "hello")]);
        Assert.Equal("<｜hy_begin▁of▁sentence｜><｜hy_User｜>hello<｜hy_Assistant｜>", text);
    }

    [Fact]
    public void UserTurn_WithoutGenerationPrompt_DoesNotGlueAssistant()
    {
        string text = ChatTemplate.RenderHunyuanDense([new ChatMessage("user", "hello")], addGenerationPrompt: false);
        Assert.Equal("<｜hy_begin▁of▁sentence｜><｜hy_User｜>hello<｜hy_place▁holder▁no▁8｜>", text);
        Assert.DoesNotContain("<｜hy_Assistant｜>", text);
    }

    [Fact]
    public void SystemThenUser_MatchesOfficialHyMt2Jinja()
    {
        string text = ChatTemplate.RenderHunyuanDense(
        [
            new ChatMessage("system", "sys"),
            new ChatMessage("user", "hi"),
        ]);
        Assert.Equal(
            "<｜hy_begin▁of▁sentence｜>sys<｜hy_place▁holder▁no▁3｜><｜hy_User｜>hi<｜hy_Assistant｜>",
            text);
    }
}
