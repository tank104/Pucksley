using System.Text.Json.Nodes;
using Pucksley.Plugins.Music.Tests.Support;
using Pucksley.Plugins.Spotify;

namespace Pucksley.Plugins.Music.Tests;

/// <summary>Choosing what to play from search results.</summary>
public sealed class SpotifyPickerTests
{
    static readonly IReadOnlyList<SpotifyCandidate> MassiveAttack = SpotifyPicker.FromSearch(JsonNode.Parse(FakeSpotify.MassiveAttackSearch));

    [Fact]
    public void Search_results_are_read_skipping_nulls()
    {
        Assert.Equal(["track", "track", "album", "artist", "playlist"], MassiveAttack.Select(c => c.Type));
        var teardrop = MassiveAttack[0];
        Assert.Equal(("Teardrop", "Massive Attack", "Mezzanine", 0, 70), (teardrop.Name, teardrop.Artist, teardrop.Album, teardrop.Rank, teardrop.Popularity));
        Assert.Equal("Spotify", MassiveAttack.Single(c => c.Type == "playlist").Artist);
    }

    [Fact]
    public void An_artists_name_plays_the_artist() =>
        Assert.Equal("spotify:artist:6FXMGgJwohJLUSr5nVlf9X", SpotifyPicker.Choose("massive attack", null, MassiveAttack)!.Uri);

    [Theory]
    [InlineData("Teardrop by Massive Attack")]
    [InlineData("teardrop massive attack")]
    [InlineData("Teardrop")]
    public void A_songs_name_plays_the_song(string query) =>
        Assert.Equal("spotify:track:67Hna13dNDkZvBpTXRIaOJ", SpotifyPicker.Choose(query, null, MassiveAttack)!.Uri);

    [Fact]
    public void A_title_track_plays_its_album()
    {
        var candidates = new[]
        {
            new SpotifyCandidate("track", "spotify:track:t", "Mezzanine", "Massive Attack", "Mezzanine"),
            new SpotifyCandidate("album", "spotify:album:a", "Mezzanine", "Massive Attack"),
        };
        Assert.Equal("spotify:album:a", SpotifyPicker.Choose("mezzanine", null, candidates)!.Uri);
    }

    [Fact]
    public void A_much_more_popular_song_beats_an_obscure_artist_of_the_same_name()
    {
        var candidates = new[]
        {
            new SpotifyCandidate("track", "spotify:track:adele", "Hello", "Adele", "25", 0, 85),
            new SpotifyCandidate("artist", "spotify:artist:hello", "Hello", Rank: 0, Popularity: 12),
        };
        Assert.Equal("spotify:track:adele", SpotifyPicker.Choose("hello", null, candidates)!.Uri);
    }

    [Fact]
    public void A_type_narrows_the_choice()
    {
        Assert.Equal("spotify:album:49MNmJhZQewjt06rpwp6QR", SpotifyPicker.Choose("mezzanine", "album", MassiveAttack)!.Uri);
        Assert.Equal("spotify:playlist:37i9dQZF1DX0hvgh1kSuSq", SpotifyPicker.Choose("massive attack", "playlist", MassiveAttack)!.Uri);
        Assert.Null(SpotifyPicker.Choose("anything", "artist", MassiveAttack.Where(c => c.Type != "artist").ToList()));
    }

    [Fact]
    public void A_vague_query_plays_spotifys_top_track()
    {
        Assert.Equal("spotify:track:67Hna13dNDkZvBpTXRIaOJ", SpotifyPicker.Choose("trip hop from bristol", null, MassiveAttack)!.Uri);
        Assert.Null(SpotifyPicker.Choose("nothing", null, []));
    }

    [Theory]
    [InlineData("track", "\"Teardrop\" by Massive Attack")]
    [InlineData("album", "the album Mezzanine by Massive Attack")]
    [InlineData("artist", "Massive Attack")]
    [InlineData("playlist", "the playlist This Is Massive Attack")]
    public void Choices_describe_themselves(string type, string expected) =>
        Assert.Equal(expected, MassiveAttack.First(c => c.Type == type).Describe());
}
