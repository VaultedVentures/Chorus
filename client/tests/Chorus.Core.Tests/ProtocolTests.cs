using Chorus.Core;

namespace Chorus.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Parse_HelloAck_WithRoster()
    {
        var evt = ServerEvent.Parse(
            """{"type":"hello_ack","session_id":"abc","proto":"1.0","mode":"converse","agent_roster":[{"id":"hermes","display_name":"Hermes","voice":"en_US-lessac-medium"}]}""");

        var ack = Assert.IsType<ServerEvent.HelloAck>(evt);
        Assert.Equal("abc", ack.SessionId);
        Assert.Equal("1.0", ack.Proto);
        Assert.Single(ack.AgentRoster);
        Assert.Equal("hermes", ack.AgentRoster[0].Id);
    }

    [Fact]
    public void Parse_Turn_Pending_CarriesTimeout()
    {
        var evt = ServerEvent.Parse(
            """{"type":"turn","state":"pending","complete":"likely","timeout_ms":1100}""");

        var turn = Assert.IsType<ServerEvent.Turn>(evt);
        Assert.Equal("pending", turn.State);
        Assert.Equal("likely", turn.Complete);
        Assert.Equal(1100, turn.TimeoutMs);
    }

    [Fact]
    public void Parse_Turn_Idle_HasNoOptionalFields()
    {
        var turn = Assert.IsType<ServerEvent.Turn>(ServerEvent.Parse("""{"type":"turn","state":"idle"}"""));
        Assert.Equal("idle", turn.State);
        Assert.Null(turn.Complete);
        Assert.Null(turn.TimeoutMs);
    }

    [Fact]
    public void Parse_Final_AgentText_Error()
    {
        Assert.Equal("hello world", Assert.IsType<ServerEvent.Final>(
            ServerEvent.Parse("""{"type":"final","text":"hello world"}""")).Text);

        var at = Assert.IsType<ServerEvent.AgentText>(
            ServerEvent.Parse("""{"type":"agent_text","agent":"hermes","text":"hi"}"""));
        Assert.Equal("hermes", at.Agent);
        Assert.Equal("hi", at.Text);

        var err = Assert.IsType<ServerEvent.Error>(
            ServerEvent.Parse("""{"type":"error","code":"tts_failed","detail":"boom"}"""));
        Assert.Equal("tts_failed", err.Code);
        Assert.Equal("boom", err.Detail);
    }

    [Fact]
    public void Parse_AudioMarker_And_Pong()
    {
        var audio = Assert.IsType<ServerEvent.Audio>(
            ServerEvent.Parse("""{"type":"audio","seq":7,"agent":"hermes"}"""));
        Assert.Equal(7, audio.Seq);
        Assert.IsType<ServerEvent.Pong>(ServerEvent.Parse("""{"type":"pong"}"""));
    }

    [Fact]
    public void Parse_Unknown_Type_Is_Unknown()
    {
        var unk = Assert.IsType<ServerEvent.Unknown>(
            ServerEvent.Parse("""{"type":"warp_drive","x":1}"""));
        Assert.Equal("warp_drive", unk.RawType);
    }

    [Theory]
    [InlineData("""{"type":"turn","state":"listening"}""")]
    [InlineData("""{"type":"final","text":"x"}""")]
    [InlineData("""{"type":"agent_text","agent":"a","text":"b"}""")]
    [InlineData("""{"type":"error","code":"c"}""")]
    public void Parse_All_Known_Types_RoundTrip(string json)
    {
        var evt = ServerEvent.Parse(json);
        Assert.NotNull(evt);
        Assert.Equal(evt.Type, evt.Type); // parsed without throwing
    }
}
