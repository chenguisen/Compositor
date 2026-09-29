using Compositor.Core.Document;

namespace Compositor.Core.Tests;

/// <summary>
/// The ruler's ticks: which round steps a zoom asks for, how long each tick is, and what its number reads.
/// </summary>
public class RulerScaleTests
{
    [Fact]
    public void TheStepIsTheFirstRoundOneAtLeastSeventyPointsLong()
    {
        // At 1:1 a step of 100 document pixels is 100 points across, and 50 would only be 50.
        Assert.Equal(100, RulerScale.MajorStep(1));
        Assert.Equal(100, RulerScale.MajorStep(0.9));
        // Zoomed in, the step shrinks; zoomed out, it grows, and never stops being a round number.
        Assert.Equal(50, RulerScale.MajorStep(1.5));
        Assert.Equal(20, RulerScale.MajorStep(4));
        Assert.Equal(10, RulerScale.MajorStep(10));
        Assert.Equal(5, RulerScale.MajorStep(20));
        Assert.Equal(1, RulerScale.MajorStep(100));
        Assert.Equal(1_000, RulerScale.MajorStep(0.1));
        // Whatever the zoom, the step is the smallest round one that reaches 70 points, so it is never
        // shorter than that and never more than the next round one up.
        foreach (var zoom in new[] { 0.01, 0.05, 0.2, 0.7, 1.3, 3, 7, 15, 32 })
        {
            var step = RulerScale.MajorStep(zoom);
            Assert.True(step * zoom >= 70 - 1e-9, $"a step of {step} at {zoom} is under 70 points");
            Assert.True(step * zoom <= 70 * 2.5 + 1e-9, $"a step of {step} at {zoom} is far over 70 points");
        }
    }

    [Fact]
    public void EveryTenthTickIsNumberedAndTheHalfWayOneIsLonger()
    {
        var ticks = RulerScale.Between(0, 250, 1);
        // The step is 100, so 0, 100 and 200 are numbered and 50 and 150 are the mid ticks.
        Assert.Equal(RulerTickKind.Major, ticks.Single(tick => tick.Value == 0).Kind);
        Assert.Equal(RulerTickKind.Major, ticks.Single(tick => tick.Value == 100).Kind);
        Assert.Equal(RulerTickKind.Major, ticks.Single(tick => tick.Value == 200).Kind);
        Assert.Equal(RulerTickKind.Mid, ticks.Single(tick => tick.Value == 50).Kind);
        Assert.Equal(RulerTickKind.Mid, ticks.Single(tick => tick.Value == 150).Kind);
        Assert.Equal(RulerTickKind.Minor, ticks.Single(tick => tick.Value == 10).Kind);
        Assert.Equal(RulerTickKind.Minor, ticks.Single(tick => tick.Value == 240).Kind);
        // Ten fine ticks to each numbered one, in order and with both ends covered.
        Assert.Equal(0, ticks[0].Value);
        Assert.Equal(250, ticks[^1].Value);
        Assert.Equal(26, ticks.Count);
        Assert.True(ticks.Zip(ticks.Skip(1)).All(pair => pair.Second.Value > pair.First.Value));
    }

    [Fact]
    public void ARulerScrolledPastTheEdgeStillNumbersItsTicks()
    {
        // The view is over the middle of a wide document: the ticks are numbered by where they are on the
        // document, not by where they are on the screen.
        var ticks = RulerScale.Between(4_980, 5_130, 1);
        Assert.Equal(4_980, ticks[0].Value);
        Assert.Equal(5_000, ticks.Single(tick => tick.Kind == RulerTickKind.Major && tick.Value == 5_000).Value);
        Assert.Equal("5000", RulerScale.Label(5_000));
        Assert.Equal("0", RulerScale.Label(0));
        Assert.Equal("100", RulerScale.Label(100.4));
        // Ticks in the negative half of the document, which a canvas scrolled past its top-left reaches.
        var above = RulerScale.Between(-140, -10, 1);
        Assert.Equal(-140, above[0].Value);
        Assert.Equal(RulerTickKind.Major, above.Single(tick => tick.Value == -100).Kind);
    }

    [Fact]
    public void AViewTooFarOutToDrawTicksGivesNoneRatherThanMillions()
    {
        Assert.Empty(RulerScale.Between(0, double.PositiveInfinity, 1));
        Assert.Empty(RulerScale.Between(double.NaN, 10, 1));
        // A range of a million document pixels at a step of one is not a ruler anyone can read.
        Assert.Empty(RulerScale.Between(0, 1_000_000, 100));
    }
}
