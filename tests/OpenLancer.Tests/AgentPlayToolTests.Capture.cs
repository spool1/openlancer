using System;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Play;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The tool <c>play_capture</c>: an image of the game of Play in OpenLancer after a delay, with the
/// Play of a fake editor. The fake gives the PNG image that the game wrote, and the tool makes a larger image smaller.
/// </summary>
public sealed partial class AgentPlayToolTests
{
    /// <summary>A PNG image of four by two pixels, as the game writes it: a white row on a black row.</summary>
    private static readonly byte[] FramePng = AgentImages.Png(4, 2, Frame()).Png;

    private static byte[] Frame()
    {
        var bottomUp = new byte[4 * 4 * 2];
        bottomUp.AsSpan(16).Fill(255);
        return bottomUp;
    }

    /// <summary>The image of the running game, with its size and the state of Play. The delay of the call goes to the
    /// Play of the editor, and an image that fits its limits keeps its data.</summary>
    [Fact]
    public void ACaptureGivesTheImageOfTheRunningGame()
    {
        var (host, editor) = Editor();
        editor.PlayStatus = PlayStatus.Running;
        editor.Captured = new PlayCaptureResult(PlayStatus.Running) { Png = FramePng, Width = 4, Height = 2 };

        var result = Call(host, "play_capture", """{ "delay": 2.5 }""");
        var now = Call(host, "play_capture");

        Assert.False(result.IsError, result.Text);
        Assert.Empty(SchemaCheck.Problems(AgentTools.Find("play_capture")!.OutputSchema, result.Content));
        Assert.Equal((4, 2, "running"), ((int)result.Content["width"]!, (int)result.Content["height"]!,
            (string?)result.Content["play"]));
        Assert.NotNull(result.Content["rev"]);
        var image = Assert.Single(result.Images);
        Assert.Same(FramePng, image.Png);
        Assert.Equal((4, 2), (image.Width, image.Height));
        Assert.False(now.IsError, now.Text);
        Assert.Equal([TimeSpan.FromSeconds(2.5), TimeSpan.Zero], editor.Captures);
    }

    /// <summary>A frame larger than width or height becomes smaller by a whole factor and keeps its shape, as a large
    /// panel of capture_panel does. A frame that fits keeps its data.</summary>
    [Fact]
    public void ALargerFrameGivesASmallerImageWithTheSameShape()
    {
        var (host, editor) = Editor();
        editor.PlayStatus = PlayStatus.Starting;
        editor.Captured = BlackFrame(400, 200, PlayStatus.Starting);

        var same = Call(host, "play_capture", """{ "width": 400, "height": 200 }""");
        var narrow = Ok(host, "play_capture", """{ "width": 200, "height": 200 }""");
        var low = Call(host, "play_capture", """{ "width": 2048, "height": 50 }""");

        Assert.Same(editor.Captured!.Png, same.Images[0].Png);
        Assert.Equal((200, 100, "starting"), ((int)narrow["width"]!, (int)narrow["height"]!, (string?)narrow["play"]));
        Assert.Equal((100, 50), ((int)low.Content["width"]!, (int)low.Content["height"]!));
        Assert.Equal((100, 50), (low.Images[0].Width, low.Images[0].Height));
    }

    /// <summary>The result of a capture of a black frame of a size.</summary>
    private static PlayCaptureResult BlackFrame(int width, int height, PlayStatus play = PlayStatus.Running) =>
        new(play) { Png = AgentImages.Png(width, height, new byte[4 * width * height]).Png, Width = width, Height = height };

    /// <summary>Only the game of Play in OpenLancer gives an image. Play answers at once when no such game runs. The
    /// cases are idle, a running game of a play copy only, and a game that stops. The report of a crash that waits for
    /// a validation is a case too. The tool then gives refused with NoGame, not CaptureFailed.</summary>
    [Theory]
    [InlineData("Idle", false)]
    [InlineData("Idle", true)]
    [InlineData("Stopping", false)]
    [InlineData("Validating", false)]
    public void WithoutAGameOfPlayTheToolGivesNoGame(string status, bool playCopy)
    {
        var (host, editor) = Editor();
        var play = Enum.Parse<PlayStatus>(status);
        editor.PlayStatus = play;
        editor.GameRuns = playCopy;
        editor.Captured = PlayCaptureResult.WithoutGame(play);

        var error = Error(host, "play_capture");

        Assert.Equal(("refused", EditorAgentPlay.NoGameDetail,
            "No OpenLancer game runs. Start one with play or mission_play."), error);
        Assert.Single(editor.Captures);
    }

    /// <summary>A game that gives no image, for example because it closed or did not answer in time, gives refused with
    /// the reason of Play. Data that is not an image gives refused too.</summary>
    [Fact]
    public void AGameThatGivesNoImageGivesCaptureFailed()
    {
        var (host, editor) = Editor();
        editor.PlayStatus = PlayStatus.Running;
        editor.Captured = PlayCaptureResult.Failed(PlayStatus.Running, PlayController.CaptureLateText);

        var late = Error(host, "play_capture");
        editor.Captured = new PlayCaptureResult(PlayStatus.Running) { Png = [1, 2, 3], Width = 4000, Height = 2000 };
        var broken = Error(host, "play_capture");

        Assert.Equal(("refused", EditorAgentPlay.CaptureFailedDetail, "The game did not give the image in 10 s."), late);
        Assert.Equal(("refused", EditorAgentPlay.CaptureFailedDetail), (broken.Code, broken.Detail));
        Assert.StartsWith("The image of the game is not a PNG image that the editor can read.", broken.Message);
    }

    /// <summary>The delay goes from 0 to 60 s. The tail has the time of the delay beyond the tail limit, so a long delay
    /// does not end in the error limit.</summary>
    [Theory]
    [InlineData("""{ "delay": -0.5 }""")]
    [InlineData("""{ "delay": 60.5 }""")]
    [InlineData("""{ "delay": "5" }""")]
    public void TheDelayGoesFrom0To60Seconds(string arguments)
    {
        var (host, editor) = Editor();
        editor.PlayStatus = PlayStatus.Running;
        editor.Captured = new PlayCaptureResult(PlayStatus.Running) { Png = FramePng, Width = 4, Height = 2 };

        var error = Error(host, "play_capture", arguments);
        Ok(host, "play_capture", """{ "delay": 60 }""");

        Assert.Equal(("invalid_argument", "BadArgument"), (error.Code, error.Detail));
        Assert.Equal("The argument 'delay' needs a number from 0 to 60.", error.Message);
        Assert.Equal([TimeSpan.FromSeconds(60)], editor.Captures);
        Assert.Equal(TimeSpan.FromSeconds(120), AgentTools.Find("play_capture")!.TailLimit(new AgentLimits()));
    }

    /// <summary>A side above the image limit gives limit, as for the other image tools. The limit is a setting. A side
    /// under 16 pixels gives invalid_argument.</summary>
    [Fact]
    public void ASideAboveTheImageLimitGivesLimit()
    {
        var (host, editor) = Editor();
        editor.PlayStatus = PlayStatus.Running;

        var wide = Error(host, "play_capture", """{ "width": 2049 }""");
        host.TestLimits = new AgentLimits { RenderPixels = 512 };
        var high = Error(host, "play_capture", """{ "height": 513 }""");
        var small = Error(host, "play_capture", """{ "width": 15 }""");

        Assert.Equal(("limit", "RenderSize", "One image can have at most 2048 pixels on each side. Give a smaller width or height."),
            wide);
        Assert.Equal(("limit", "RenderSize"), (high.Code, high.Detail));
        Assert.Equal(("invalid_argument", "BadArgument"), (small.Code, small.Detail));
        Assert.Empty(editor.Captures);
    }

    /// <summary>Without the arguments, the image has at most the image limit on each side.</summary>
    [Fact]
    public void TheDefaultSizeIsTheImageLimit()
    {
        var (host, editor) = Editor();
        editor.PlayStatus = PlayStatus.Running;
        editor.Captured = BlackFrame(1024, 512);
        host.TestLimits = new AgentLimits { RenderPixels = 512 };

        var limited = Ok(host, "play_capture");

        Assert.Equal((512, 256), ((int)limited["width"]!, (int)limited["height"]!));
    }

    /// <summary>The tool is in the group Play, so it gives not_available while the user hides that group. The host
    /// without a window has no Play. The tool changes nothing, so it also works while the user pauses the agent
    /// edits.</summary>
    [Fact]
    public void TheHiddenGroupAndTheHostWithoutAWindowGiveNotAvailable()
    {
        var (host, editor) = Editor();
        editor.PlayStatus = PlayStatus.Running;
        editor.Captured = new PlayCaptureResult(PlayStatus.Running) { Png = FramePng, Width = 4, Height = 2 };

        host.TestPaused = true;
        Ok(host, "play_capture");
        host.Hidden.Add(ToolGroup.Play);
        var hidden = Error(host, "play_capture");
        var headless = Error(new TestAgentHost(ws.Session(null)), "play_capture");

        Assert.Equal(("not_available", "GroupHidden"), (hidden.Code, hidden.Detail));
        Assert.Equal(("not_available", "EditorOnly",
            "Only the editor host has the tool play_capture. Start the agent server in the editor."), headless);
        Assert.Single(editor.Captures);
    }

    /// <summary>A cancel of the client ends the wait of the call. The capture of Play ends on its own.</summary>
    [Fact]
    public async Task ACancelEndsTheWaitForTheImage()
    {
        var (host, editor) = Editor();
        editor.PlayStatus = PlayStatus.Running;
        using var cancel = new CancellationTokenSource();

        var tail = Tail(host, "{}", cancel.Token, "play_capture");
        Assert.False(tail.IsCompleted);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tail.WaitAsync(Wait, Token));
        Assert.Single(editor.Captures);
    }
}
