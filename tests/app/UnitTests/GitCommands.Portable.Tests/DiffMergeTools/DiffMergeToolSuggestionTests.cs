using GitCommands.DiffMergeTools;

namespace GitCommandsTests.DiffMergeTools;

public class DiffMergeToolSuggestionTests
{
    [Test]
    public void Tools_lists_the_recommended_tool_first([Values] DiffMergeToolType toolType)
    {
        DiffMergeToolSuggestion.Tools(toolType).First().Should().Be(DiffMergeToolSuggestion.RecommendedTool);
    }

    [Test]
    public void SuggestCommand_uses_the_given_path_and_the_tool_arguments()
    {
        DiffMergeToolSuggestion.SuggestCommand("diffweave", DiffMergeToolType.Diff, "/opt/dw/diffweave")
            .Should().Be("\"/opt/dw/diffweave\" --L1 \"$LOCAL\" --L2 \"$REMOTE\" \"$LOCAL\" \"$REMOTE\"");
        DiffMergeToolSuggestion.SuggestCommand("DiffWeave", DiffMergeToolType.Merge, "\"/opt/dw/diffweave\"")
            .Should().StartWith("\"/opt/dw/diffweave\" --auto ").And.EndWith("-o \"$MERGED\" \"$BASE\" \"$LOCAL\" \"$REMOTE\"");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("no-such-tool")]
    public void SuggestCommand_returns_null_for_unknown_tools(string? toolName)
    {
        DiffMergeToolSuggestion.SuggestCommand(toolName, DiffMergeToolType.Diff).Should().BeNull();
    }

    [Test]
    public void SuggestPath_returns_null_without_a_tool()
    {
        DiffMergeToolSuggestion.SuggestPath(" ").Should().BeNull();
    }
}
