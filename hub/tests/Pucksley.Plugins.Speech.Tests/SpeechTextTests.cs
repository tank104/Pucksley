namespace Pucksley.Plugins.Speech.Tests;

public class SpeechTextTests
{
    [Theory]
    [InlineData("**Hello** there", "Hello there")]
    [InlineData("# Weather\nIt's *sunny* today.", "Weather. It's sunny today.")]
    [InlineData("## Forecast ##\nFine.", "Forecast. Fine.")]
    [InlineData("Run `npm install` first.", "Run npm install first.")]
    [InlineData("See https://example.com/a?b=c for more.", "See link for more.")]
    [InlineData("Details at https://metservice.com.", "Details at link.")]
    [InlineData("Go to www.nzherald.co.nz today", "Go to link today")]
    [InlineData("Docs: <https://example.com/x>", "Docs: link")]
    [InlineData("Read [the forecast](https://metservice.com/towns) now.", "Read the forecast now.")]
    [InlineData("Logo: ![](https://x.nz/logo.png)", "Logo: link")]
    [InlineData("- Milk\n- Eggs\n- Bread", "Milk. Eggs. Bread")]
    [InlineData("Shopping:\n* Milk\n* Eggs", "Shopping: Milk. Eggs")]
    [InlineData("1. Preheat\n2. Bake", "1. Preheat. 2. Bake")]
    [InlineData("snake_case stays, _emphasis_ goes", "snake_case stays, emphasis goes")]
    [InlineData("C# and F# are languages.", "C# and F# are languages.")]
    [InlineData("It's 20°C ☀️ today 😀", "It's 20°C today")]
    [InlineData("Done ✅ and ⭐ rated", "Done and rated")]
    [InlineData("a  \t b\n\n\nc", "a b. c")]
    [InlineData("> Quoted line", "Quoted line")]
    [InlineData("Above\n---\nBelow", "Above. Below")]
    [InlineData("~~old~~ new", "old new")]
    [InlineData("```js\nlet x = 1;\n```", "let x = 1;")]
    [InlineData("| Day | Temp |\n|---|---|\n| Mon | 18 |", "Day, Temp. Mon, 18")]
    [InlineData("He said \"hi\"\nThen left", "He said \"hi\". Then left")]
    [InlineData("Kia ora, Aotearoa! Ka kite anō.", "Kia ora, Aotearoa! Ka kite anō.")]
    [InlineData("Line one\r\nLine two", "Line one. Line two")]
    public void Cleans_markdown_for_speech(string input, string expected) => Assert.Equal(expected, SpeechText.Clean(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("***")]
    [InlineData("😀🎉")]
    [InlineData("```\n```")]
    public void Nothing_speakable_becomes_empty(string? input) => Assert.Equal("", SpeechText.Clean(input));

    [Fact]
    public void Long_text_splits_after_sentences_within_the_limit()
    {
        var text = string.Join(" ", Enumerable.Repeat("This is a sentence.", 60));
        var pieces = SpeechText.SplitForLimit(text, 100).ToList();

        Assert.True(pieces.Count > 1);
        Assert.All(pieces, p => Assert.InRange(p.Length, 1, 100));
        Assert.All(pieces, p => Assert.EndsWith(".", p));
        Assert.Equal(text, string.Join(" ", pieces));
    }

    [Fact]
    public void Long_text_without_sentences_splits_at_spaces()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 100));
        var pieces = SpeechText.SplitForLimit(text, 42).ToList();

        Assert.All(pieces, p => Assert.InRange(p.Length, 1, 42));
        Assert.All(pieces, p => Assert.DoesNotContain("  ", p));
        Assert.Equal(text, string.Join(" ", pieces));
    }

    [Fact]
    public void Short_text_is_one_piece() => Assert.Equal(["Hello."], SpeechText.SplitForLimit("Hello.", 4000));
}
