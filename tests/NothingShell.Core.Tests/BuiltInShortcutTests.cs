using NothingShell.Core.Brain;
using Xunit;

namespace NothingShell.Core.Tests;

public class BuiltInShortcutTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1));

    private static IEnumerable<BuiltInShortcut> Commands(ShortcutMemory memory) => memory.BuiltIn.SelectMany(s => s.Commands);

    [Fact]
    public void EveryPhraseAndCommandIsListedExactlyOnce()
    {
        var commands = Commands(new ShortcutMemory()).ToArray();
        Assert.Equal(ShortcutSeeds.All.Select(s => s.Command).Distinct().Count(), commands.Length);
        Assert.Equal(commands.Length, commands.Select(c => c.Command).Distinct().Count());
        var phrases = commands.SelectMany(c => c.Phrases).ToArray();
        Assert.Equal(ShortcutSeeds.All.Count, phrases.Length);
        Assert.Equal(phrases.Length, phrases.Distinct().Count());
    }

    [Fact]
    public void SectionsAreNamedAndNotEmpty()
    {
        var sections = new ShortcutMemory().BuiltIn;
        Assert.Equal(ShortcutSeeds.Sections.Select(s => s.Title), sections.Select(s => s.Title));
        Assert.All(sections, s => Assert.NotEmpty(s.Commands));
        Assert.Contains(sections, s => s.Title == "Hardware");
        Assert.Contains(sections, s => s.Title == "Git");
    }

    [Theory]
    [InlineData("che ore sono")]
    [InlineData("numero di serie")]
    [InlineData("programmi che partono all'avvio")]
    public void PhrasesReadAsTheyAreWritten(string phrase) =>
        Assert.Contains(Commands(new ShortcutMemory()), c => c.Phrases.Contains(phrase));

    [Fact]
    public void PhrasesOfTheSameCommandStayTogether()
    {
        var memory = Assert.Single(Commands(new ShortcutMemory()), c => c.Phrases.Contains("che ore sono"));
        Assert.Contains("what time is it", memory.Phrases);
        Assert.Equal("Get-Date", memory.Command);
    }

    [Fact]
    public void NothingWasUsedOnTheFirstDay()
    {
        Assert.All(Commands(new ShortcutMemory()), c =>
        {
            Assert.Equal(0, c.Uses);
            Assert.False(c.WasUsed);
        });
    }

    [Fact]
    public void UsesAddUpAcrossThePhrasesAndStayOutOfLearned()
    {
        var memory = new ShortcutMemory();
        var command = ShortcutSeeds.All.First(s => s.Phrase == "processi").Command;
        memory.Record("processi", command, true, Now);
        memory.Record("processes", command, true, Now);
        var listed = Assert.Single(Commands(memory), c => c.Command == command);
        Assert.Equal(2, listed.Uses);
        Assert.True(listed.WasUsed);
        Assert.Equal(Now, listed.Shortcut.LastUsed);
        Assert.Empty(memory.Learned);
    }

    [Fact]
    public void AForgottenPhraseDisappears()
    {
        var memory = new ShortcutMemory();
        Assert.True(memory.Forget("ram libera"));
        Assert.DoesNotContain(Commands(memory), c => c.Phrases.Contains("ram libera"));
        Assert.Contains(Commands(memory), c => c.Phrases.Contains("memoria libera"));
    }

    [Fact]
    public void ACorrectedPhraseMovesToLearned()
    {
        var memory = new ShortcutMemory();
        memory.Record("ram libera", "Get-Process", true, Now);
        Assert.DoesNotContain(Commands(memory), c => c.Phrases.Contains("ram libera"));
        Assert.Contains(memory.Learned, s => s.Phrase == "ram libera" && s.Command == "Get-Process");
    }

    [Fact]
    public void ASavedFileKeepsTheListAndItsUses()
    {
        var path = Path.GetTempFileName();
        try
        {
            var first = new ShortcutMemory(path);
            var command = ShortcutSeeds.All.First(s => s.Phrase == "processi").Command;
            first.Record("processi", command, true, Now);
            first.Record("spazio libero pc", "Get-PSDrive", true, Now);

            var again = new ShortcutMemory(path);
            var phrases = Commands(again).SelectMany(c => c.Phrases).ToArray();
            Assert.Equal(ShortcutSeeds.All.Count, phrases.Length);
            Assert.Equal(phrases.Length, phrases.Distinct().Count());
            Assert.Equal(1, Assert.Single(Commands(again), c => c.Command == command).Uses);
            Assert.Equal("spazio libero pc", Assert.Single(again.Learned).Phrase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithoutSeedsThereIsNothingToList() => Assert.Empty(new ShortcutMemory(seeds: []).BuiltIn);
}
