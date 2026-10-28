using System.IO;
using NothingShell.Core.Dev;

namespace NothingShell.App.Services;

/// <summary>
/// GitHub Actions dopo un push (docs/12-sviluppo.md): <c>gh run list</c> nel repository ogni 15 s, finché le
/// esecuzioni partite dopo il push sono concluse (al massimo un'ora). Si segue un push alla volta, l'ultimo.
/// </summary>
public sealed class CiWatcher : IDisposable
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NoRunsTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxWatch = TimeSpan.FromHours(1);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public const string MissingGh = "CI: installa e autentica GitHub CLI (gh auth login)";

    private CancellationTokenSource? _watch;

    /// <summary>Lo stato della CI seguita (null: nessuna).</summary>
    public CiStatus? Current { get; private set; }

    /// <summary>Riga grigia della pagina Lavoro quando la CI non si può seguire, altrimenti null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Stato o problema cambiato (sul thread della UI).</summary>
    public event Action? Changed;

    /// <summary>Le esecuzioni sono tutte concluse.</summary>
    public event Action<CiStatus>? Finished;

    /// <summary>Segue la CI del branch corrente di <paramref name="folder"/> dopo un push fatto ora.</summary>
    public void Watch(string folder) => _ = WatchAsync(folder, DateTimeOffset.Now, branch: null);

    /// <summary>Rilancia i job falliti e torna a seguire la stessa CI.</summary>
    public async Task RerunAsync(CiStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        foreach (var run in status.Runs.Where(r => r.Failed))
            await DevTools.RunAsync("gh", $"run rerun {run.Id} --failed", status.Folder, CommandTimeout);
        var since = status.Runs.Count > 0 ? status.Runs.Min(r => r.CreatedAt) : DateTimeOffset.Now;
        _ = WatchAsync(status.Folder, since, status.Branch);
    }

    public void Stop()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
        if (Current is null && Problem is null)
            return;
        Current = null;
        Problem = null;
        Changed?.Invoke();
    }

    public void Dispose() => Stop();

    private async Task WatchAsync(string folder, DateTimeOffset since, string? branch)
    {
        Stop();
        var watch = new CancellationTokenSource(MaxWatch);
        _watch = watch;
        var token = watch.Token;
        try
        {
            if (!Directory.Exists(folder))
                return;
            branch ??= await BranchAsync(folder);
            if (branch is null || token.IsCancellationRequested)
                return;
            await Task.Delay(FirstDelay, token);
            var started = DateTimeOffset.Now;
            while (!token.IsCancellationRequested)
            {
                var result = await DevTools.RunAsync("gh",
                    $"run list --branch \"{branch}\" --limit 10 --json {CiRuns.JsonFields}", folder, CommandTimeout);
                if (token.IsCancellationRequested)
                    return;
                if (result is not { ExitCode: 0 } ok)
                {
                    Publish(null, MissingGh);
                    return;
                }
                var status = new CiStatus(folder, branch, CiRuns.Since(CiRuns.Parse(ok.Output), since));
                if (status.Runs.Count == 0)
                {
                    if (DateTimeOffset.Now - started > NoRunsTimeout)
                    {
                        Publish(null, null); // niente CI per questo branch
                        return;
                    }
                }
                else
                {
                    Publish(status, null);
                    if (status.State != CiState.Pending)
                    {
                        Finished?.Invoke(status);
                        return;
                    }
                }
                await Task.Delay(Interval, token);
            }
        }
        catch (OperationCanceledException)
        {
            // un push più nuovo, o un'ora passata
        }
        finally
        {
            if (_watch == watch)
            {
                _watch = null;
                if (Current is { State: CiState.Pending })
                    Publish(null, Problem);
            }
            watch.Dispose();
        }
    }

    private static async Task<string?> BranchAsync(string folder) =>
        await DevTools.RunAsync("git", "rev-parse --abbrev-ref HEAD", folder, CommandTimeout) is { ExitCode: 0 } result
        && result.Output.Trim() is { Length: > 0 } branch && branch != "HEAD"
            ? branch
            : null;

    private void Publish(CiStatus? status, string? problem)
    {
        Current = status;
        Problem = problem;
        Changed?.Invoke();
    }
}
