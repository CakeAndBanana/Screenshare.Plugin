using System.Text.Json;
using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin.Tests.Networking;

// SignalR's JSON protocol serializes camelCase and reads case-insensitively, which Web defaults match.
public class RoomModelsTests
{
    private static readonly JsonSerializerOptions HubJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RoomDirectory_FromARelayThatPredatesRegions_HasNoRegions()
    {
        const string json = """{"rooms":[],"bannedRoomIds":[]}""";

        RoomDirectory directory = JsonSerializer.Deserialize<RoomDirectory>(json, HubJson)!;

        Assert.Null(directory.Regions);
    }

    [Fact]
    public void RoomDirectory_WithRegions_ReadsThem()
    {
        const string json = """{"rooms":[],"bannedRoomIds":[],"regions":["eu","na"]}""";

        RoomDirectory directory = JsonSerializer.Deserialize<RoomDirectory>(json, HubJson)!;

        Assert.Equal(["eu", "na"], directory.Regions);
    }

    [Fact]
    public void CreateRoomRequest_SendsTheRegionUnderTheNameTheRelayReads()
    {
        string json = JsonSerializer.Serialize(new CreateRoomRequest("Room", true, null, 4, "na"), HubJson);

        Assert.Contains("\"region\":\"na\"", json);
    }
}
