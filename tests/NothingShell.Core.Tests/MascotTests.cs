using NothingShell.Core.Design;
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
    public void AnAlertSurprisesItForAMoment()
    {
        var brain = new MascotBrain(new Random(8));
        brain.React(MascotMood.Surprised, Awake);
        var pose = brain.Pose(Awake + Step, null, Awake, greeting: false);
        Assert.True(pose.Open > 1.2);
        Assert.True(pose.LookY < 0); // guarda in su, verso l'avviso
        Assert.Null(brain.MoodAt(Awake + MascotBrain.SurpriseLength));
        Assert.True(brain.Pose(Awake + MascotBrain.SurpriseLength, null, Awake, greeting: false).Open <= 1);
    }

    [Fact]
    public void FinishedWorkMakesItSmileAndHop()
    {
        var brain = new MascotBrain(new Random(9));
        brain.React(MascotMood.Happy, Awake);
        var poses = Enumerable.Range(0, 20).Select(i => brain.Pose(Awake + (Step * i), null, Awake, greeting: false)).ToList();
        Assert.All(poses, p => Assert.Equal(1, p.Smile));
        Assert.Contains(poses, p => p.Lift < -0.8);
        Assert.All(poses, p => Assert.InRange(p.Lift, -1, 0));
        Assert.Equal(0, brain.Pose(Awake + MascotBrain.HappyLength, null, Awake, greeting: false).Smile);
    }

    [Fact]
    public void AFailureMakesItLookDown()
    {
        var brain = new MascotBrain(new Random(10));
        brain.React(MascotMood.Sad, Awake);
        var pose = brain.Pose(Awake + Step, null, Awake, greeting: false);
        Assert.True(pose.Open < 1 && pose.LookY > 0.5);
    }

    [Fact]
    public void AReactionWakesItUpThenItGoesBackToSleep()
    {
        var brain = new MascotBrain(new Random(11));
        brain.Pose(Awake, null, MascotBrain.SleepAfter, greeting: false);
        Assert.True(brain.Asleep);
        brain.React(MascotMood.Happy, Awake + Step);
        brain.Pose(Awake + Step, null, MascotBrain.SleepAfter, greeting: false);
        Assert.False(brain.Asleep);
        brain.Pose(Awake + MascotBrain.HappyLength + Step, null, MascotBrain.SleepAfter, greeting: false);
        Assert.True(brain.Asleep);
    }

    [Fact]
    public void GreetingWakesItUp()
    {
        var brain = new MascotBrain(new Random(7));
        brain.Pose(Awake, null, MascotBrain.SleepAfter, greeting: false);
        brain.Pose(Awake + Step, null, MascotBrain.SleepAfter, greeting: true);
        Assert.False(brain.Asleep);
    }

    /// <summary>I gesti che fa da sé in <paramref name="seconds"/> secondi, guardata ogni 60 ms.</summary>
    private static List<MascotMood> Gestures(MascotBrain brain, double seconds, (double, double)? cursor = null)
    {
        var seen = new List<MascotMood>();
        MascotMood? last = null;
        for (var t = TimeSpan.Zero; t < TimeSpan.FromSeconds(seconds); t += Step)
        {
            brain.Pose(t, cursor, Awake, greeting: false);
            var mood = brain.MoodAt(t);
            if (mood is { } m && mood != last)
                seen.Add(m);
            last = mood;
        }
        return seen;
    }

    [Fact]
    public void WithoutInitiativeItOnlyReacts() => Assert.Empty(Gestures(new MascotBrain(new Random(12)), 600));

    [Fact]
    public void WithInitiativeItMakesGesturesOfItsOwn()
    {
        var often = Gestures(new MascotBrain(new Random(13)) { Initiative = MascotInitiative.Often }, 600);
        Assert.InRange(often.Count, 5, 16); // ogni 40–100 secondi
        Assert.All(often, m => Assert.True(m is MascotMood.Wink or MascotMood.LookAround or MascotMood.Hop or MascotMood.Yawn));
        Assert.True(often.Distinct().Count() >= 3);

        var sometimes = Gestures(new MascotBrain(new Random(13)) { Initiative = MascotInitiative.Sometimes }, 600);
        Assert.InRange(sometimes.Count, 1, 5); // ogni 2–5 minuti
    }

    [Fact]
    public void NoGesturesWhileTheMouseMoves() =>
        Assert.Empty(Gestures(new MascotBrain(new Random(14)) { Initiative = MascotInitiative.Often }, 600, (0.2, 0.1)));

    [Fact]
    public void ATiredMascotKeepsItsEyesHalfOpenAndOnlyYawns()
    {
        Assert.All(Watch(new MascotBrain(new Random(15)) { Tired = true }, 30), p => Assert.True(p.Open <= 0.7));
        var gestures = Gestures(new MascotBrain(new Random(15)) { Initiative = MascotInitiative.Often, Tired = true }, 600);
        Assert.NotEmpty(gestures);
        Assert.All(gestures, m => Assert.Equal(MascotMood.Yawn, m));
    }

    [Fact]
    public void AWinkClosesOneEyeOnly()
    {
        var brain = new MascotBrain(new Random(16));
        brain.React(MascotMood.Wink, Awake);
        var pose = brain.Pose(Awake + Step, null, Awake, greeting: false);
        Assert.Equal(1, Math.Abs(pose.Wink));
        Assert.Equal(1, pose.Open);
        Assert.Equal(0, brain.Pose(Awake + MascotBrain.LengthOf(MascotMood.Wink), null, Awake, greeting: false).Wink);
    }

    [Fact]
    public void EveryExpressionStaysInRangeAndEnds()
    {
        foreach (var mood in Enum.GetValues<MascotMood>())
        {
            var length = MascotBrain.LengthOf(mood);
            for (var t = TimeSpan.Zero; t < length; t += TimeSpan.FromMilliseconds(20))
            {
                var p = MascotBrain.Feel(mood, t, side: -1);
                Assert.InRange(p.Open, 0.05, 1.5);
                Assert.InRange(p.LookX, -1, 1);
                Assert.InRange(p.LookY, -1, 1);
                Assert.InRange(p.Smile, 0, 1);
                Assert.InRange(p.Lift, -1, 0);
                Assert.InRange(p.Wink, -1, 1);
            }
            var brain = new MascotBrain(new Random(17));
            brain.React(mood, Awake);
            Assert.Equal(mood, brain.MoodAt(Awake));
            Assert.Null(brain.MoodAt(Awake + length));
        }
    }

    [Fact]
    public void LookingAroundGoesFullCircle()
    {
        var poses = Enumerable.Range(0, 60).Select(i => MascotBrain.Feel(MascotMood.LookAround, TimeSpan.FromMilliseconds(i * 20))).ToList();
        Assert.True(poses.Max(p => p.LookX) > 0.85 && poses.Min(p => p.LookX) < -0.85);
        Assert.True(poses.Max(p => p.LookY) > 0.85 && poses.Min(p => p.LookY) < -0.85);
    }

    [Fact]
    public void YawnClosesTheEyesSlowlyThenReopensThemHalfway()
    {
        Assert.True(MascotBrain.Feel(MascotMood.Yawn, TimeSpan.FromMilliseconds(100)).Open > 1);
        Assert.InRange(MascotBrain.Feel(MascotMood.Yawn, TimeSpan.FromMilliseconds(450)).Open, 0.4, 0.9); // a metà strada
        Assert.True(MascotBrain.Feel(MascotMood.Yawn, TimeSpan.FromMilliseconds(900)).Open < 0.15);
        Assert.InRange(MascotBrain.Feel(MascotMood.Yawn, TimeSpan.FromMilliseconds(1750)).Open, 0.6, 0.8);
    }

    [Fact]
    public void CallingLooksAtYouAndHops()
    {
        var poses = Enumerable.Range(0, 35).Select(i => MascotBrain.Feel(MascotMood.Calling, TimeSpan.FromMilliseconds(i * 20))).ToList();
        Assert.All(poses, p => Assert.True(p.Open > 1 && p.LookX == 0 && p.LookY > 0)); // occhi grandi, verso chi sta davanti
        Assert.Contains(poses, p => p.Lift < -0.5);
    }

    [Fact]
    public void TalkingLooksAtItsText() =>
        Assert.True(MascotBrain.Feel(MascotMood.Talking, TimeSpan.FromMilliseconds(100)).LookX > 0.5); // il testo le sta a destra

    /// <summary>L'espressione ogni 20 ms per tutta la sua durata.</summary>
    private static List<MascotPose> Frames(MascotMood mood, int side = 1) =>
        Enumerable.Range(0, (int)(MascotBrain.LengthOf(mood).TotalMilliseconds / 20))
            .Select(i => MascotBrain.Feel(mood, TimeSpan.FromMilliseconds(i * 20), side)).ToList();

    [Fact]
    public void PettedItSquintsHappilyAndSways()
    {
        var purr = Frames(MascotMood.Purr).Take(70).ToList();
        Assert.All(purr, p => Assert.True(p.Smile == 1 && p.Open < 0.7));
        Assert.True(purr.Max(p => p.LookX) > 0.25 && purr.Min(p => p.LookX) < -0.25); // si struscia di qua e di là
        Assert.Contains(purr, p => p.Lift < -0.1); // e freme
        Assert.True(Frames(MascotMood.Purr)[^1].Open > 0.9); // alla fine riapre gli occhi
    }

    [Fact]
    public void TickledItWinksOneEyeThenTheOther()
    {
        var giggle = Frames(MascotMood.Giggle, side: 1);
        Assert.True(giggle[2].Wink > 0.5 && giggle[15].Wink < -0.5);
        Assert.Contains(giggle, p => p.Lift < -0.5);
    }

    [Fact]
    public void WavedAtItSwingsItsEyesThenWinks()
    {
        var wave = Frames(MascotMood.WaveBack, side: -1);
        Assert.True(wave.Take(45).Max(p => p.LookX) > 0.75 && wave.Take(45).Min(p => p.LookX) < -0.75);
        Assert.Equal(-1, MascotBrain.Feel(MascotMood.WaveBack, TimeSpan.FromMilliseconds(1000), side: -1).Wink);
    }

    [Fact]
    public void ItNodsTwice()
    {
        var looks = Frames(MascotMood.Nod).Select(p => p.LookY).ToList();
        var ups = looks.Zip(looks.Skip(1)).Count(l => l.First >= 0 && l.Second < 0);
        Assert.Equal(2, ups);
        Assert.True(Frames(MascotMood.Nod).Where(p => p.LookY < 0).All(p => p.Lift < -0.2)); // guardando in su il viso si alza
    }

    [Fact]
    public void APokeSqueezesItsEyesThenItStaresAtYou()
    {
        Assert.True(MascotBrain.Feel(MascotMood.Poked, TimeSpan.FromMilliseconds(50)).Open < 0.2);
        var stare = MascotBrain.Feel(MascotMood.Poked, TimeSpan.FromMilliseconds(400));
        Assert.True(stare.Open > 1.2 && stare.LookY > 0.5); // tu sei sotto
    }

    [Fact]
    public void OffendedItLooksTheOtherWayThenPeeksBack()
    {
        Assert.True(MascotBrain.Feel(MascotMood.Huffy, TimeSpan.FromSeconds(1), side: 1).LookX < -0.8);
        Assert.True(MascotBrain.Feel(MascotMood.Huffy, TimeSpan.FromSeconds(1), side: -1).LookX > 0.8);
        Assert.True(Math.Abs(Frames(MascotMood.Huffy)[^1].LookX) < 0.1);
    }
}
