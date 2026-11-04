using NothingShell.Core.Notch;

namespace NothingShell.Core.Tests;

public class MascotBrainTests
{
    private static readonly TimeSpan Awake = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(60);

    /// <summary>Le pose ogni 60 ms per <paramref name="seconds"/> secondi, come le chiede l'interfaccia.</summary>
    private static List<MascotPose> Watch(MascotBrain brain, double seconds, (double, double)? cursor = null, TimeSpan? idle = null, bool greeting = false)
    {
        var poses = new List<MascotPose>();
        for (var t = TimeSpan.Zero; t < TimeSpan.FromSeconds(seconds); t += Step)
            poses.Add(brain.Pose(t, cursor, idle ?? Awake, greeting));
        return poses;
    }

    [Fact]
    public void BlinksEveryFewSeconds()
    {
        var poses = Watch(new MascotBrain(new Random(1)), 30);
        var blinks = poses.Zip(poses.Skip(1)).Count(p => p.First.Open > 0.5 && p.Second.Open < 0.5);
        Assert.InRange(blinks, 5, 16);
        Assert.True(poses.Count(p => p.Open < 0.5) < poses.Count / 10); // gli occhi restano quasi sempre aperti
    }

    [Fact]
    public void FollowsTheCursorAndClampsIt()
    {
        var pose = new MascotBrain(new Random(2)).Pose(Awake, (3, -0.25), Awake, greeting: false);
        Assert.Equal(1, pose.LookX);
        Assert.Equal(-0.25, pose.LookY);
    }

    [Fact]
    public void LooksAroundWhenTheCursorRests()
    {
        var poses = Watch(new MascotBrain(new Random(3)), 30);
        Assert.Contains(poses, p => Math.Abs(p.LookX) > 0.05);
        Assert.Contains(poses, p => p.LookX == 0);
    }

    [Fact]
    public void SmilesWhenGreeted() =>
        Assert.Equal(1, new MascotBrain(new Random(4)).Pose(Awake, null, Awake, greeting: true).Smile);

    [Fact]
    public void FallsAsleepAndWakesUpWideEyed()
    {
        var brain = new MascotBrain(new Random(5));
        var asleep = brain.Pose(TimeSpan.FromSeconds(10), null, MascotBrain.SleepAfter, greeting: false);
        Assert.True(brain.Asleep);
        Assert.True(asleep.Open < 0.2);

        var woken = brain.Pose(TimeSpan.FromSeconds(11), null, TimeSpan.Zero, greeting: false);
        Assert.False(brain.Asleep);
        Assert.True(woken.Open > 1);
        Assert.Equal(1, brain.Pose(TimeSpan.FromSeconds(11) + MascotBrain.WakeLength, null, TimeSpan.Zero, greeting: false).Open);
    }

    [Fact]
    public void GetsDrowsyBeforeSleeping()
    {
        var pose = new MascotBrain(new Random(6)).Pose(Awake, null, MascotBrain.SleepAfter * 0.8, greeting: false);
        Assert.InRange(pose.Open, 0.01, 0.6);
    }

    [Fact]
    public void GreetingWakesItUp()
    {
        var brain = new MascotBrain(new Random(7));
        brain.Pose(Awake, null, MascotBrain.SleepAfter, greeting: false);
        brain.Pose(Awake + Step, null, MascotBrain.SleepAfter, greeting: true);
        Assert.False(brain.Asleep);
    }
}
