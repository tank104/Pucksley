using Pucksley.Plugins.Shared;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Home.Tests;

public class NamesTests
{
    [Fact]
    public void Names_made_of_ignored_words_still_match_with_an_article()
    {
        string[] plugs = ["Lamp", "Fan", "Heater"];
        Assert.True(NameMatcher.Score("the lamp", "Lamp") >= 0.55); // the SDK matcher handles articles now too
        Assert.Equal("Lamp", Names.Best("the lamp", plugs, p => p));
        Assert.Equal("Lamp", Names.Best("my lamp", plugs, p => p));
        Assert.Equal("Heater", Names.Best("the heater", plugs, p => p));
        Assert.Null(Names.Best("the kettle", plugs, p => p));
        Assert.Equal("TV", Names.Best("the TV", ["TV", "Radio"], t => t));
    }

    [Fact]
    public void Formats_numbers_invariantly()
    {
        Assert.Equal("21", Text.Num(21.0));
        Assert.Equal("20.5", Text.Num(20.5));
        Assert.Equal("21.0", Text.Fixed(21));
        Assert.Equal("A, B and C", Text.List(["A", "B", "C"]));
        Assert.Equal("Kitchen 30%", Text.Activity("Kitchen 30%"));
        var cut = Text.Activity("Kitchen lights are on and bright");
        Assert.True(cut.Length <= 24);
        Assert.EndsWith("…", cut);
        Assert.Equal("lounge-lamp", Text.Slug("Lounge Lamp!"));
    }
}
