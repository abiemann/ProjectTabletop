using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectTabletop.App;
using ProjectTabletop.Vision;

namespace ProjectTabletop.Tests;

public sealed class DoubleBarMarkerSettingsTests
{
    private static readonly BlackTipProfile Profile = new(1, .08, .1, .001, 4, 2, .4);

    [Fact]
    public void EarlierSettingsKeepCameraProfilesAndDiscardObsoleteSelection()
    {
        var profiles = new Dictionary<string, BlackTipProfile>
        {
            ["camera-1"] = Profile,
            ["camera-2"] = Profile with { NormalizedArea = .0011 }
        };
        string previous = JsonSerializer.Serialize(new
        {
            Version = 1, Cameras = profiles,
            ActiveModes = new Dictionary<string, string> { ["camera-1"] = "eye", ["camera-2"] = "bar" }
        });

        var saved = DoubleBarMarkerSettings.Parse(previous);

        Assert.Equal(profiles.Count, saved.Cameras.Count);
        foreach (var (camera, profile) in profiles) Assert.Equal(profile, saved.Cameras[camera]);
        var rewritten = JsonNode.Parse(JsonSerializer.Serialize(saved))!.AsObject();
        Assert.Equal(new[] { "Version", "Cameras" }, rewritten.Select(property => property.Key));
        Assert.Equal(Profile, DoubleBarMarkerSettings.Parse(rewritten.ToJsonString()).Cameras["camera-1"]);
    }

    [Fact]
    public void CamerasOnlySettingsDoNotRequireASelectionProperty()
    {
        string json = JsonSerializer.Serialize(new DoubleBarMarkerSettings(1,
            new Dictionary<string, BlackTipProfile> { ["camera"] = Profile }));

        Assert.Equal(Profile, Assert.Single(DoubleBarMarkerSettings.Parse(json).Cameras).Value);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Cameras\":{}}")]
    [InlineData("{\"Version\":1,\"Cameras\":null}")]
    [InlineData("{\"Version\":1,\"Cameras\":{\"camera\":null}}")]
    public void InvalidCameraSettingsAreRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => DoubleBarMarkerSettings.Parse(json));

    [Fact]
    public void SingleBarsAndInvalidDoubleBarsCannotBecomeSharedMarkerProfiles()
    {
        foreach (var profile in new[] { Profile with { BarCount = 1, SpacingRatio = 0 },
            Profile with { NormalizedArea = 0 } })
        {
            string json = JsonSerializer.Serialize(new DoubleBarMarkerSettings(1,
                new Dictionary<string, BlackTipProfile> { ["camera"] = profile }));
            Assert.Throws<InvalidDataException>(() => DoubleBarMarkerSettings.Parse(json));
        }
    }
}
