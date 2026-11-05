using NothingShell.Core.Brain;

namespace NothingShell.Core.Tests;

/// <summary>Scorciatoie scritte a mano in Impostazioni › Sviluppo (docs/15-comandi-cervello.md).</summary>
public class HandMadeShortcutTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    // La prima volta le espressioni dei comandi critici si preparano: con tutta la suite in parallelo possono superare il
    // limite di 100 ms, e un controllo scaduto conta (per prudenza) come critico.
    static HandMadeShortcutTests() => CommandSafety.Classify("Get-Date");

    private static ShortcutMemory Empty() => new(null, []);

    [Fact]
    public void AHandMadeShortcutAnswersAtOnce()
    {
        var memory = Empty();
        Assert.True(memory.Remember("file del progetto nothing", "Get-ChildItem C:\\dev\\nothing", Now));
        var decision = memory.Decide("mostrami i file del progetto nothing")!;
        Assert.Equal("Get-ChildItem C:\\dev\\nothing", decision.Command);
        Assert.Equal(DecisionSource.Exact, decision.Source);
        Assert.True(decision.RunNow);
        Assert.Equal("Get-ChildItem C:\\dev\\nothing", memory.Find("file progetto nothing")!.Command);
    }

    [Fact]
    public void ACommandThatChangesThingsStillAsks()
    {
        var memory = Empty();
        Assert.True(memory.Remember("avvia il sito", "npm run dev --prefix C:\\dev\\sito", Now));
        Assert.False(memory.Decide("avvia il sito")!.RunNow);
    }

    [Fact]
    public void ASlotMakesATemplate()
    {
        var memory = Empty();
        Assert.Null(ShortcutMemory.WhyNot("apri il progetto {0}", "code C:\\dev\\{0}"));
        Assert.True(memory.Remember("apri il progetto {0}", "code C:\\dev\\{0}", Now));
        var decision = memory.Decide("apri progetto nothing")!;
        Assert.Equal("code C:\\dev\\nothing", decision.Command);
        Assert.Equal(DecisionSource.Template, decision.Source);
        var template = Assert.Single(memory.Learned);
        Assert.True(template.IsTemplate);
        Assert.Equal("apri progetto {0}", template.Phrase);
        Assert.Same(template, memory.Find(template.Phrase));

        // Il valore non può portarsi dietro altri comandi o parametri.
        Assert.Null(memory.Decide("apri progetto x;rm"));
        Assert.Equal("code C:\\dev\\force", memory.Decide("apri progetto -force")!.Command);

        Assert.True(memory.Forget(template.Phrase));
        Assert.Empty(memory.Learned);
        Assert.Null(memory.Decide("apri progetto nothing"));
    }

    [Fact]
    public void RememberingTheSamePhraseReplacesIt()
    {
        var memory = Empty();
        memory.Remember("build", "dotnet build", Now);
        memory.Remember("build", "dotnet build -c Release", Now);
        Assert.Equal("dotnet build -c Release", Assert.Single(memory.Learned).Command);
    }

    [Fact]
    public void AHandMadeShortcutCanReplaceABuiltInOne()
    {
        var memory = new ShortcutMemory();
        var seed = memory.Find("ram libera");
        Assert.True(seed!.Seed);
        memory.Remember("ram libera", "Get-Date", Now);
        Assert.Equal("Get-Date", memory.Find("ram libera")!.Command);
        Assert.False(memory.Find("ram libera")!.Seed);
    }

    [Theory]
    [InlineData("", "Get-Date", "frase")]
    [InlineData("che ore sono", "  ", "comando")]
    [InlineData("due righe", "Get-Date\nGet-Process", "una riga")]
    [InlineData("entra", "ssh pi@casa -o password=ciao", "chiavi")]
    [InlineData("apri {0} {0}", "code {0}", "un {0}")]
    [InlineData("apri {0}", "code {0} {0}", "un {0}")]
    [InlineData("{0}", "code {0}", "almeno una parola")]
    [InlineData("apri progetto {0}", "code C:\\dev", "anche nel comando")]
    [InlineData("apri progetto", "code {0}", "anche nella frase")]
    [InlineData("cancella {0}", "Remove-Item {0}", "critico")]
    public void WhyNotExplainsWhatIsWrong(string phrase, string command, string expected)
    {
        var why = ShortcutMemory.WhyNot(phrase, command);
        Assert.NotNull(why);
        Assert.Contains(expected, why, StringComparison.OrdinalIgnoreCase);
        Assert.False(Empty().Remember(phrase, command, Now));
    }

    [Theory]
    [InlineData("che ore sono", "Get-Date")]
    [InlineData("svuota cestino", "Clear-RecycleBin -Force")]
    [InlineData("processo {0}", "Get-Process {0}")]
    public void WhyNotAcceptsGoodShortcuts(string phrase, string command) =>
        Assert.Null(ShortcutMemory.WhyNot(phrase, command));
}
