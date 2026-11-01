using NothingShell.Core.RemoteAccess;
using NothingShell.Core.Brain;
using NothingShell.Core.Notch;

namespace NothingShell.Core.Tests;

public class RemoteAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly RemoteComputer Luca = new("Computer di Luca", "192.168.1.50", UserName: "luca");
    private static readonly RemoteComputer Marta = new("Computer di Marta", "192.168.1.51", UserName: "marta");

    private static ShortcutMemory NoCommands() => new(null, []);

    private static RequestRoute Route(string input, params RemoteComputer[] computers) =>
        RequestRouter.Route(input, computers, NoCommands());

    [Theory]
    [InlineData("collegati al computer di luca")]
    [InlineData("Collegati al computer di Luca!")]
    [InlineData("connettiti al pc di luca")]
    [InlineData("puoi collegarti al computer di luca?")]
    [InlineData("colegati al computer di luca")]
    [InlineData("apri il computer di luca")]
    [InlineData("desktop remoto computer di luca")]
    [InlineData("computer di luca")]
    public void TheExactNameIsUsedWithoutAsking(string request)
    {
        var route = Route(request, Luca, Marta);
        Assert.Equal(Luca, route.Sure?.Computer);
    }

    [Theory]
    [InlineData("collegati al computer di marco")] // un altro nome
    [InlineData("collegati al computer di paola")] // parola nuova
    [InlineData("collegati al computer di luna")] // una lettera da "luca"
    [InlineData("collegati al compiuter di luca")] // errore di battitura
    [InlineData("collegati a luca")] // il nome solo in parte
    [InlineData("collegati al computer")] // ne basta uno, ma quale?
    [InlineData("collegati")]
    public void WhatIsOnlySimilarNeverStartsByItself(string request) =>
        Assert.Null(Route(request, Luca, Marta).Sure);

    [Fact]
    public void AnotherNameIsShownAsDifferentNotAsTheAnswer()
    {
        var route = Route("collegati al computer di marco", Luca);
        Assert.Empty(route.Likely);
        var different = Assert.Single(route.Different);
        Assert.Equal(Luca, different.Computer);
        Assert.Equal(["marco"], different.Missing);
        Assert.Equal("Computer di Marco", route.Connection.SuggestedName);
        Assert.StartsWith("Nuovo computer: Computer di Marco", route.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesThatDifferByALetterAreNeverTheSameComputer()
    {
        var paolo = new RemoteComputer("Computer di Paolo", "10.0.0.1");
        var route = Route("collegati al computer di paola", paolo);
        Assert.Null(route.Sure);
        Assert.Equal(MatchQuality.Close, route.Likely.Single().Quality);
    }

    [Fact]
    public void NumbersAreNeverTypos()
    {
        var one = new RemoteComputer("Server1", "10.0.0.1");
        Assert.Empty(Route("collegati a server2", one).Options);
        Assert.False(RequestMatcher.IsClose("server1", "server2"));
    }

    [Fact]
    public void TyposAreOfferedForConfirmation()
    {
        var route = Route("collegati al compiuter di luca", Luca, Marta);
        var option = Assert.Single(route.Likely);
        Assert.Equal(Luca, option.Computer);
        Assert.Equal(MatchQuality.Close, option.Quality);
        Assert.Contains("Invio per scegliere", route.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void AGenericRequestListsEveryMatchToChooseFrom()
    {
        var route = Route("collegati al computer", Luca, Marta);
        Assert.Equal(2, route.Likely.Count);
        Assert.Contains("Computer di Luca, Computer di Marta", route.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectingWithoutANameListsAllComputers() =>
        Assert.Equal(2, Route("collegati", Luca, Marta).Likely.Count);

    [Fact]
    public void TheMostUsedComputerComesFirst()
    {
        var route = Route("collegati al computer", Luca, Marta with { Successes = 4 });
        Assert.Equal("Computer di Marta", route.Options[0].Title);
    }

    [Fact]
    public void TwoExactMatchesMeanChoosing()
    {
        var memory = new ShortcutMemory(null, []);
        memory.Remember("computer di luca", "Get-Date", Now);
        var route = RequestRouter.Route("computer di luca", [Luca], memory);
        Assert.Equal(2, route.Options.Count(o => o.Quality == MatchQuality.Exact));
        Assert.Null(route.Sure);
    }

    [Fact]
    public void LearnedCommandsAreShortcutsToo()
    {
        var memory = new ShortcutMemory(null, []);
        memory.Remember("stato del server", "ssh pi@raspberry uptime", Now);
        var route = RequestRouter.Route("stato del server", [], memory);
        Assert.Equal("ssh pi@raspberry uptime", route.Sure?.Command);
        Assert.StartsWith("→ stato server · ssh pi@raspberry uptime", route.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatEnterDoesDependsOnTheMatch()
    {
        var known = new[] { Luca, Marta };
        Assert.Equal(RequestAction.Use, Route("collegati al computer di luca", known).ActionFor());
        Assert.Equal(RequestAction.Choose, Route("collegati al computer di luca", known).ActionFor(choose: true));
        Assert.Equal(RequestAction.Choose, Route("collegati al computer", known).ActionFor());
        Assert.Equal(RequestAction.Choose, Route("collegati al computer di marco", known).ActionFor()); // forse è una svista
        Assert.Equal(RequestAction.NewComputer, Route("collegati al computer di marco").ActionFor());
        Assert.Equal(RequestAction.Ask, Route("che ore sono a tokyo", known).ActionFor());
        Assert.Equal(RequestAction.Ask, Route("quanta ram ha il computer di luca", known).ActionFor()); // nomina Luca, non chiede di collegarsi
    }

    [Fact]
    public void AnEmptyCredentialListMeansNoPassword()
    {
        Assert.False(RemoteDesktop.HasCredential(["Credenziali attualmente archiviate per TERMSRV/x:", "", "* NESSUNA *"]));
        Assert.False(RemoteDesktop.HasCredential(["Currently stored credentials for TERMSRV/x:", "", "* NONE *"]));
        Assert.True(RemoteDesktop.HasCredential(["Destinazione: LegacyGeneric:target=TERMSRV/x", "Tipo: Generica", "Utente: luca"]));
    }

    [Fact]
    public void ManyPhrasesForOneCommandAreOneOption()
    {
        var route = RequestRouter.Route("memoria", [], new ShortcutMemory());
        Assert.Single(route.Options, o => o.Command!.Contains("Win32_OperatingSystem", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewRequestMayGoToTheModel()
    {
        var route = RequestRouter.Route("che ore sono a tokyo", [], new ShortcutMemory());
        Assert.Empty(route.Likely); // "che ore sono" è una scorciatoia, ma a Tokyo è un'altra domanda
        Assert.Equal(RequestAction.Ask, route.ActionFor());
        Assert.False(route.Connection.IsConnection);
        Assert.Contains("assistente", route.Preview, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("collegati al computer di luca", true, "Computer di Luca")]
    [InlineData("connettimi al pc di marta", true, "PC di Marta")]
    [InlineData("vorrei collegarmi al server dell'ufficio", true, "Server dell'ufficio")]
    [InlineData("apri il computer di luca", true, "Computer di Luca")]
    [InlineData("apri il browser", false, null)]
    [InlineData("spegni il computer", false, null)]
    [InlineData("quanta ram è libera", false, null)]
    public void ConnectionsAreRecognized(string text, bool connection, string? name)
    {
        var phrase = ConnectionPhrase.Parse(text);
        Assert.Equal(connection, phrase.IsConnection);
        Assert.Equal(name, phrase.SuggestedName);
    }

    [Fact]
    public void AnAddressInTheRequestFillsTheForm()
    {
        Assert.Equal("192.168.1.50", ConnectionPhrase.Parse("collegati a 192.168.1.50").SuggestedAddress);
        Assert.Equal("10.0.0.9:3390", ConnectionPhrase.Parse("collegati a 10.0.0.9:3390").SuggestedAddress);
        Assert.Null(ConnectionPhrase.Parse("collegati al computer di luca").SuggestedAddress);
    }

    [Fact]
    public void VerbsAreRemovedOnlyAtTheBeginning()
    {
        Assert.Equal(["computer", "collega"], ConnectionPhrase.Parse("computer del collega").Tokens);
        Assert.Equal(["computer", "collega"], ConnectionPhrase.Parse("collegati al computer del collega").Tokens);
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50", 3389)]
    [InlineData(" 192.168.1.50:3390 ", "192.168.1.50", 3390)]
    [InlineData("pc-luca", "pc-luca", 3389)]
    [InlineData("pc-luca.local:13389", "pc-luca.local", 13389)]
    [InlineData("fe80::1", "fe80::1", 3389)]
    [InlineData("[fe80::1]:3390", "fe80::1", 3390)]
    public void AddressesAreParsed(string text, string host, int port)
    {
        Assert.Null(RemoteDesktop.ParseAddress(text, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pc luca")]
    [InlineData("/admin")]
    [InlineData("-x")]
    [InlineData("pc\"; calc")]
    [InlineData("pc';calc;'")]
    [InlineData("rdp://pc")]
    [InlineData("pc:99999")]
    [InlineData("pc:abc")]
    [InlineData("[fe80::1")]
    [InlineData("[pc]:3389")]
    public void AnythingElseIsNotAnAddress(string text) =>
        Assert.NotNull(RemoteDesktop.ParseAddress(text, out _, out _));

    [Fact]
    public void CommandsUseTheAddress()
    {
        var other = new RemoteComputer("Server", "fe80::1", 3390);
        Assert.Equal("/v:192.168.1.50", RemoteDesktop.MstscArgument(Luca));
        Assert.Equal("/v:[fe80::1]:3390", RemoteDesktop.MstscArgument(other));
        Assert.Equal("TERMSRV/192.168.1.50", RemoteDesktop.CredentialTarget(Luca));
        Assert.Equal("cmdkey /list:TERMSRV/192.168.1.50", RemoteDesktop.CredentialScript(Luca));
        Assert.Contains("ConnectAsync('192.168.1.50', 3389)", RemoteDesktop.ReachScript(Luca), StringComparison.Ordinal);
        Assert.Contains("-OwningProcess 42 -RemotePort 3390", RemoteDesktop.WaitScript(other, 42), StringComparison.Ordinal);
    }

    [Fact]
    public void AComputerNeedsANameAndAnAddress()
    {
        Assert.NotNull(RemoteDesktop.Build("", "10.0.0.1", null, false).Problem);
        Assert.NotNull(RemoteDesktop.Build("Luca", "", null, false).Problem);
        Assert.NotNull(RemoteDesktop.Build("Luca", "10.0.0.1", null, hasPassword: true).Problem);
        Assert.Equal(new RemoteComputer("Luca", "10.0.0.1", 3390, "luca"), RemoteDesktop.Build(" Luca ", "10.0.0.1:3390", " luca ", true).Computer);
        Assert.Null(RemoteDesktop.Build("Luca", "10.0.0.1", null, false).Problem); // senza password la chiede mstsc
    }

    [Fact]
    public void AChoiceBecomesAnotherNameForTheComputer()
    {
        var store = new RemoteComputerStore();
        store.Save(Luca);
        store.Save(Marta);

        const string phrase = "collegati al compiuter di luca";
        Assert.Null(RequestRouter.Route(phrase, store.All, NoCommands()).Sure);
        store.RecordSuccess(Luca, phrase, Now);

        var route = RequestRouter.Route(phrase, store.All, NoCommands());
        Assert.Equal("Computer di Luca", route.Sure?.Title);
        Assert.Equal(1, route.Sure?.Computer?.Successes);
    }

    [Fact]
    public void AnAliasThatBecomesAmbiguousIsForgotten()
    {
        var store = new RemoteComputerStore();
        store.Save(Luca);
        store.RecordSuccess(Luca, "collegati a luca", Now);
        Assert.Equal("Computer di Luca", RequestRouter.Route("collegati a luca", store.All, NoCommands()).Sure?.Title);

        store.Save(new RemoteComputer("Portatile di Luca", "192.168.1.60"));
        var route = RequestRouter.Route("collegati a luca", store.All, NoCommands());
        Assert.Null(route.Sure);
        Assert.Equal(2, route.Likely.Count);
    }

    [Fact]
    public void AnAliasCannotBeTheNameOfAnotherComputer()
    {
        var store = new RemoteComputerStore();
        store.Save(Luca);
        store.Save(Marta);
        store.RecordSuccess(Luca, "computer di marta", Now);
        Assert.Equal("Computer di Marta", RequestRouter.Route("computer di marta", store.All, NoCommands()).Sure?.Title);
    }

    [Fact]
    public void SecretsAreNeverLearned()
    {
        var store = new RemoteComputerStore();
        store.Save(Luca);
        store.RecordSuccess(Luca, "collegati a luca con password pippo", Now);
        Assert.Empty(store.All[0].Aliases ?? []);
    }

    [Fact]
    public void TwoComputersCannotHaveTheSameName()
    {
        var store = new RemoteComputerStore();
        store.Save(Luca);
        Assert.NotNull(store.Save(new RemoteComputer("PC di Luca", "10.0.0.2"))); // pc = computer
        Assert.Null(store.Save(Luca with { Host = "10.0.0.3" }, Luca));
        Assert.Equal("10.0.0.3", Assert.Single(store.All).Host);
    }

    [Fact]
    public void TheFileHoldsNoPasswordAndRejectsBadAddresses()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ns-computers-{Guid.NewGuid():n}.json");
        try
        {
            var store = new RemoteComputerStore(path);
            store.Save(Luca);
            Assert.DoesNotContain("password", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Luca.Host, new RemoteComputerStore(path).All.Single().Host);

            File.WriteAllText(path, """[{"name":"Cattivo","host":"/admin calc","port":3389}]""");
            Assert.Empty(new RemoteComputerStore(path).All);
            File.WriteAllText(path, "non è json");
            Assert.Empty(new RemoteComputerStore(path).All);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WritingKeepsTheNotchOpenWithoutTheMouse()
    {
        var machine = new NotchStateMachine();
        var now = TimeSpan.FromSeconds(10);
        machine.OnPointerEnter();
        machine.Hold(true, pointerOver: true, now);
        machine.OnPointerLeave(now);
        Assert.Null(machine.CollapseDeadline);
        Assert.False(machine.Tick(now + TimeSpan.FromMinutes(5)));
        Assert.Equal(NotchState.Expanded, machine.State);

        machine.Hold(false, pointerOver: false, now);
        Assert.NotNull(machine.CollapseDeadline);
        Assert.True(machine.Tick(now + NotchStateMachine.LeaveDelay));
        Assert.Equal(NotchState.Idle, machine.State);
    }

    [Fact]
    public void ReleasingWithTheMouseOverStaysOpen()
    {
        var machine = new NotchStateMachine();
        machine.OnPointerEnter();
        machine.Hold(true, pointerOver: true, TimeSpan.Zero);
        machine.Hold(false, pointerOver: true, TimeSpan.Zero);
        Assert.Null(machine.CollapseDeadline);
    }

    [Fact]
    public void SendingARequestClosesTheNotchEvenWithTheMouseOver()
    {
        var machine = new NotchStateMachine();
        machine.OnPointerEnter();
        machine.Hold(true, pointerOver: true, TimeSpan.Zero);
        Assert.True(machine.Collapse());
        Assert.Equal(NotchState.Idle, machine.State);
        Assert.False(machine.Held);
    }

    [Fact]
    public void TheHomeMakesRoomForTheField()
    {
        Assert.Equal(NotchLayout.HomeHeight, NotchLayout.Expanded(NotchPage.Home).Height);
        Assert.Equal(NotchLayout.HomeMediaHeight, NotchLayout.Expanded(NotchPage.Home, media: true).Height);
        Assert.True(NotchLayout.Expanded(NotchPage.Home, media: true).Height + NotchLayout.TopMargin <= NotchLayout.Host.Height);
    }
}
