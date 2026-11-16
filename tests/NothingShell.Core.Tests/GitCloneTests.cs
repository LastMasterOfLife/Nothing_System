using NothingShell.Core.Brain;
using NothingShell.Core.Dev;

namespace NothingShell.Core.Tests;

public class GitCloneTests
{
    [Theory]
    [InlineData("clona https://github.com/a/progetto.git", "https://github.com/a/progetto.git", null)]
    [InlineData("fai il clone di git@github.com:a/progetto.git con i sottomoduli", "git@github.com:a/progetto.git", true)]
    [InlineData("clonami https://gitlab.com/a/b senza sottomoduli.", "https://gitlab.com/a/b", false)]
    [InlineData("puoi clonare il repository https://github.com/a/b.", "https://github.com/a/b", null)]
    public void PhrasesWithAnAddressAreClones(string text, string url, bool? recursive)
    {
        var request = GitClone.From(text);
        Assert.NotNull(request);
        Assert.Equal(url, request.Url);
        Assert.Equal(recursive, request.Recursive);
    }

    [Theory]
    [InlineData("clona il progetto nothing")]
    [InlineData("apri https://github.com/a/b")]
    [InlineData("git status")]
    [InlineData("git clone https://github.com/a/b; rm -r x")]
    [InlineData("git clone $url")]
    [InlineData("git clone")]
    public void OtherInputsAreNot(string text) => Assert.Null(GitClone.From(text));

    [Fact]
    public void CommandsKeepFolderRecursiveAndOptions()
    {
        var request = GitClone.FromCommand("git clone --recurse-submodules -b dev --depth 1 https://github.com/a/b.git \"C:\\progetti\\mio b\"");
        Assert.NotNull(request);
        Assert.Equal("https://github.com/a/b.git", request.Url);
        Assert.Equal(@"C:\progetti\mio b", request.Folder);
        Assert.True(request.Recursive);
        Assert.Equal(["-b", "dev", "--depth", "1"], request.Options);
    }

    [Theory]
    [InlineData("https://github.com/a/progetto.git", "progetto")]
    [InlineData("https://github.com/a/progetto/", "progetto")]
    [InlineData("git@github.com:a/progetto.git", "progetto")]
    [InlineData("git@host:progetto.git", "progetto")]
    public void TheNameIsTheOneGitUses(string url, string name) => Assert.Equal(name, GitClone.RepositoryName(url));

    [Fact]
    public void TheSuggestionIsTheBaseFolderPlusTheName()
    {
        Assert.Equal(@"C:\repos\b", GitClone.SuggestedFolder(new GitCloneRequest("https://x/a/b.git"), @"C:\repos"));
        Assert.Equal(@"C:\repos\altro", GitClone.SuggestedFolder(new GitCloneRequest("https://x/a/b.git", "altro"), @"C:\repos"));
        Assert.Equal(@"D:\x", GitClone.SuggestedFolder(new GitCloneRequest("https://x/a/b.git", @"D:\x"), @"C:\repos"));
    }

    [Fact]
    public void ArgumentsAndCommand()
    {
        var request = new GitCloneRequest("https://x/a/b.git", Options: ["--depth", "1"]);
        var choice = new GitCloneChoice(@"C:\mio repo\b", true);
        Assert.Equal(["clone", "--recursive", "--depth", "1", "--", "https://x/a/b.git", @"C:\mio repo\b"], GitClone.Arguments(request, choice));
        Assert.Equal(@"git clone --recursive --depth 1 https://x/a/b.git 'C:\mio repo\b'", GitClone.Command(request, choice));
        Assert.Equal(@"git clone https://x/a/b.git C:\b", GitClone.Command(request with { Options = null }, new GitCloneChoice(@"C:\b", false)));
    }

    [Fact]
    public void OnlyEmptyOrNewFoldersAreAccepted()
    {
        var root = Path.Combine(Path.GetTempPath(), "ns-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(GitClone.Problem(Path.Combine(root, "nuova")));
            Assert.Null(GitClone.Problem(root));
            File.WriteAllText(Path.Combine(root, "x.txt"), "x");
            Assert.NotNull(GitClone.Problem(root));
            Assert.NotNull(GitClone.Problem("relativa"));
            Assert.NotNull(GitClone.Problem("  "));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Runner : IAssistantRunner
    {
        public List<string> Confirmed { get; } = [];

        public Task<AssistantRun> RunReadOnlyAsync(string command, CancellationToken token) => throw new InvalidOperationException(command);

        public Task<AssistantRun> RunConfirmedAsync(string command, CancellationToken token)
        {
            Confirmed.Add(command);
            return Task.FromResult(new AssistantRun(AssistantRunStatus.Ok, string.Empty));
        }
    }

    private sealed class View(GitCloneChoice? choice) : IAssistantView
    {
        public List<(GitCloneRequest Request, string Folder)> Asked { get; } = [];

        public List<string> Answers { get; } = [];

        public List<string> Notes { get; } = [];

        public void Thinking()
        {
        }

        public void Running(string command)
        {
        }

        public void Finished(AssistantStep result)
        {
        }

        public Task<bool> ConfirmAsync(AssistantConfirm request, CancellationToken token) => Task.FromResult(false);

        public void Answer(string text, bool verified) => Answers.Add(text);

        public void Choices(IReadOnlyList<string> choices)
        {
        }

        public void Note(string text) => Notes.Add(text);

        public void OfferShortcut(string phrase, string command)
        {
        }

        public Task<GitCloneChoice?> AskCloneAsync(GitCloneRequest request, string folder, CancellationToken token)
        {
            Asked.Add((request, folder));
            return Task.FromResult(choice);
        }
    }

    private sealed class Model(string reply) : IAssistantModel
    {
        public string Name => "prova";

        public Task<string?> AskAsync(string system, IReadOnlyList<AssistantTurn> turns, CancellationToken token) => Task.FromResult<string?>(reply);
    }

    [Fact]
    public async Task ACloneAsksWhereAndWithWhatBeforeStarting()
    {
        var target = Path.Combine(Path.GetTempPath(), "ns-clone-" + Guid.NewGuid().ToString("N"), "b");
        var runner = new Runner();
        var view = new View(new GitCloneChoice(target, true));
        var assistant = new CommandAssistant(new ShortcutMemory(null, []), runner, @"C:\Users\prova") { CloneFolder = () => @"C:\repos" };

        await assistant.AskAsync("clona https://github.com/a/b.git", view, CancellationToken.None);

        Assert.Equal(@"C:\repos\b", Assert.Single(view.Asked).Folder);
        Assert.Equal($"git clone --recursive https://github.com/a/b.git {target}", Assert.Single(runner.Confirmed));
        Assert.Single(view.Answers);
    }

    [Fact]
    public async Task WithoutAnAnswerNothingIsCloned()
    {
        var runner = new Runner();
        var view = new View(null);
        var assistant = new CommandAssistant(new ShortcutMemory(null, []), runner, @"C:\Users\prova") { Model = new Model("COMANDO: git clone https://github.com/a/b.git") };

        await assistant.AskAsync("scarica il repo b di a", view, CancellationToken.None);

        Assert.Single(view.Asked); // anche quando lo propone il modello
        Assert.Empty(runner.Confirmed);
        Assert.Contains("Non l'ho eseguito.", view.Notes);
    }

    [Fact]
    public async Task ABusyFolderIsNotUsed()
    {
        var runner = new Runner();
        var view = new View(new GitCloneChoice(Path.GetTempPath(), false));
        var assistant = new CommandAssistant(new ShortcutMemory(null, []), runner, @"C:\Users\prova");

        await assistant.AskAsync("> git clone https://github.com/a/b.git", view, CancellationToken.None);

        Assert.Empty(runner.Confirmed);
        Assert.Single(view.Notes);
    }
}
