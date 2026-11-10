using Pucksley.Plugins.Sonos;
using Pucksley.Sdk;

namespace Pucksley.Plugins.Music.Tests;

/// <summary>Which room a request means (NameMatcher over the real household's rooms).</summary>
public sealed class SonosRoomsTests
{
    static readonly SonosTopology Real = SonosParsingTests.RealTopology();
    static readonly Func<SonosGroup, bool> NothingPlaying = _ => false;

    [Theory]
    [InlineData("Kitchen", "Kitchen")]
    [InlineData("the kitchen", "Kitchen")]
    [InlineData("kitchen speaker", "Kitchen")]
    [InlineData("Kichen", "Kitchen")]
    [InlineData("study", "Loft Study")]
    [InlineData("the loft study", "Loft Study")]
    [InlineData("the move", "Sonos Move")]
    [InlineData("sonos move", "Sonos Move")]
    public void Spoken_names_find_the_room(string spoken, string expected) =>
        Assert.Equal(expected, SonosRooms.Resolve(Real, spoken, "Office", null, NothingPlaying).RoomName);

    [Fact]
    public void An_unknown_room_lists_the_rooms()
    {
        var e = Assert.Throws<PluginException>(() => SonosRooms.Resolve(Real, "den", null, null, NothingPlaying));
        Assert.Equal("I can't find a Sonos room called 'den'. Rooms: Kitchen, Loft Study, Sonos Move.", e.Message);
    }

    [Fact]
    public void No_room_named_means_the_pucks_room() =>
        Assert.Equal("Kitchen", SonosRooms.Resolve(Real, null, "Kitchen", "Loft Study", NothingPlaying).RoomName);

    [Fact]
    public void A_puck_room_without_sonos_falls_back_to_the_default_room() =>
        Assert.Equal("Loft Study", SonosRooms.Resolve(Real, null, "Garage", "loft study", NothingPlaying).RoomName);

    [Fact]
    public void Then_the_only_group_playing()
    {
        var kitchen = Real.Players.Single(p => p.RoomName == "Kitchen");
        Assert.Equal("Kitchen", SonosRooms.Resolve(Real, null, "Bedroom", null, g => g.Coordinator == kitchen).RoomName);
    }

    [Fact]
    public void Otherwise_it_asks_which_room()
    {
        var e = Assert.Throws<PluginException>(() => SonosRooms.Resolve(Real, null, null, null, _ => true));
        Assert.Equal("Which room? Sonos rooms: Kitchen, Loft Study, Sonos Move.", e.Message);
    }

    [Fact]
    public void Implicit_rooms_need_a_closer_match_than_spoken_ones()
    {
        // "Study" said out loud finds Loft Study; a puck whose room is "Bathroom" doesn't grab it.
        Assert.Null(SonosRooms.Implicit(Real, "Bathroom", null));
        Assert.Equal("Loft Study", SonosRooms.Implicit(Real, "Study", null)!.RoomName);
    }

    [Fact]
    public void No_speakers_says_how_to_fix_it()
    {
        var e = Assert.Throws<PluginException>(() => SonosRooms.Resolve(SonosTopology.Empty, "kitchen", null, null, NothingPlaying));
        Assert.Equal(SonosRooms.NoSpeakersMessage, e.Message);
    }

    [Theory]
    [InlineData("all", true)]
    [InlineData("All rooms", true)]
    [InlineData("everywhere", true)]
    [InlineData("the whole house", true)]
    [InlineData("kitchen", false)]
    [InlineData(null, false)]
    public void All_words(string? spoken, bool expected) => Assert.Equal(expected, SonosRooms.IsAll(spoken));

    [Fact]
    public void Groups_can_be_named()
    {
        var bonded = SonosParsingTests.BondedTopology();
        Assert.Equal("Lounge + Dining Room", SonosRooms.MatchGroup(bonded, "the lounge group")!.Name);
        Assert.Equal("Lounge + Dining Room", SonosRooms.MatchGroup(bonded, "Lounge + Dining Room")!.Name);
        Assert.Null(SonosRooms.MatchGroup(bonded, "lounge"));
        Assert.Null(SonosRooms.MatchGroup(bonded, null));
    }
}
