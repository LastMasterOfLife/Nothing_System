using System.IO;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using NothingShell.Core.Brain;

namespace NothingShell.App.Services;

/// <summary>
/// Il modello dell'assistente in un processo a sé (<c>NothingShell.exe --assistant &lt;file&gt;</c>), con lo stesso dialogo di
/// <see cref="BrainWorker"/>: una riga JSON con la conversazione, una con la risposta (docs/15-comandi-cervello.md).
/// </summary>
public sealed class AssistantWorker : IDisposable
{
    private const int ContextSize = 4096;
    private const int MaxAnswerTokens = 500;
    private const int BatchSize = 512;

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly List<(LLamaToken[] Tokens, LLamaContext.SequenceState State)> _snapshots = [];

    private AssistantWorker(LLamaWeights weights, LLamaContext context)
    {
        _weights = weights;
        _context = context;
    }

    public static async Task RunAsync(string modelPath)
    {
        BrainWorker.LoadNativeLibraries();
        NativeLibraryConfig.All.WithLogCallback((_, _) => { });
        // Nelle prove su un i7-1255U oltre 8 thread su 12 la generazione non migliora.
        var threads = Math.Max(2, Environment.ProcessorCount * 2 / 3);
        var parameters = new ModelParams(modelPath)
        {
            ContextSize = ContextSize,
            GpuLayerCount = 0,
            Threads = threads,
            BatchThreads = threads,
            BatchSize = BatchSize,
        };
        using var weights = await LLamaWeights.LoadFromFileAsync(parameters);
        using var context = weights.CreateContext(parameters);
        using var worker = new AssistantWorker(weights, context);
        var utf8 = new UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };

        while (await input.ReadLineAsync() is { } line)
        {
            var chat = JsonSerializer.Deserialize<AssistantChat>(JsonSerializer.Deserialize<string>(line) ?? "null");
            await output.WriteLineAsync(JsonSerializer.Serialize(chat is null ? string.Empty : await worker.AnswerAsync(chat)));
        }
    }

    public void Dispose()
    {
        foreach (var snapshot in _snapshots)
            snapshot.State.Dispose();
    }

    private async Task<string> AnswerAsync(AssistantChat chat)
    {
        var system = ChatMl.Turn("system", chat.System);
        var reply = _context.Tokenize(ChatMl.Reply, false, true);
        var turns = chat.Turns.ToList();
        var prompt = Prompt(system, turns);
        while (prompt.Length + reply.Length + MaxAnswerTokens > ContextSize && turns.Count > 1)
        {
            turns.RemoveAt(0);
            prompt = Prompt(system, turns);
        }
        if (prompt.Length + reply.Length + MaxAnswerTokens > ContextSize)
            return string.Empty;

        // L'ultima riga del sistema è l'ora: si ferma prima, così la prima istantanea vale anche per le domande dei minuti dopo.
        var stable = _context.Tokenize(ChatMl.TurnStart("system") + chat.System[..(chat.System.LastIndexOf('\n') + 1)], false, true);

        var at = Restore(prompt);
        if (at < stable.Length && StartsWith(prompt, stable))
        {
            await FeedAsync(stable, at, stable.Length, 0, false);
            at = stable.Length;
            Remember(stable);
        }
        if (at < prompt.Length)
        {
            await FeedAsync(prompt, at, prompt.Length, 0, false);
            Remember(prompt);
        }
        var batch = await FeedAsync(reply, 0, reply.Length, prompt.Length, true);

        using var sampler = new DefaultSamplingPipeline { Temperature = 0f };
        var decoder = new StreamingTokenDecoder(_context);
        var answer = new StringBuilder();
        var position = prompt.Length + reply.Length;
        var logits = batch.TokenCount - 1;
        for (var made = 0; made < MaxAnswerTokens; made++)
        {
            var token = sampler.Sample(_context.NativeHandle, logits);
            sampler.Accept(token);
            if (token == _weights.Vocab.EOS || token == _weights.Vocab.EOT)
                break;
            decoder.Add(token);
            answer.Append(decoder.Read());
            if (answer.ToString().EndsWith(ChatMl.End, StringComparison.Ordinal))
                break;
            batch.Clear();
            batch.Add(token, position++, LLamaSeqId.Zero, true);
            await DecodeAsync(batch);
            logits = 0;
        }
        return answer.ToString().Replace(ChatMl.End, string.Empty, StringComparison.Ordinal);
    }

    private LLamaToken[] Prompt(string system, IEnumerable<AssistantTurn> turns) =>
        _context.Tokenize(system + string.Concat(turns.Select(t => ChatMl.Turn(t.Role, t.Content))), false, true);

    /// <summary>
    /// Non si può togliere solo la coda della memoria: su questi modelli la rimozione non ha effetto. Si riparte quindi
    /// dall'istantanea più lunga che combacia col prompt; quanti token sono già pronti.
    /// </summary>
    private int Restore(LLamaToken[] prompt)
    {
        var best = _snapshots.Where(s => StartsWith(prompt, s.Tokens)).OrderByDescending(s => s.Tokens.Length).FirstOrDefault();
        _context.NativeHandle.MemoryClear();
        if (best.State is null)
            return 0;
        _context.LoadState(best.State, LLamaSeqId.Zero);
        return best.Tokens.Length;
    }

    /// <summary>Tiene la prima istantanea, quella del sistema che vale per ogni domanda, e l'ultima conversazione.</summary>
    private void Remember(LLamaToken[] tokens)
    {
        var keep = _snapshots.Count > 0 && StartsWith(tokens, _snapshots[0].Tokens) ? 1 : 0;
        for (var i = _snapshots.Count - 1; i >= keep; i--)
        {
            _snapshots[i].State.Dispose();
            _snapshots.RemoveAt(i);
        }
        _snapshots.Add((tokens, _context.GetState(LLamaSeqId.Zero)));
    }

    private static bool StartsWith(LLamaToken[] tokens, LLamaToken[] start) =>
        start.Length <= tokens.Length && start.AsSpan().SequenceEqual(tokens.AsSpan(0, start.Length));

    private async Task<LLamaBatch> FeedAsync(LLamaToken[] tokens, int from, int to, int offset, bool logitsAtEnd)
    {
        var batch = new LLamaBatch();
        for (var at = from; at < to; at += BatchSize)
        {
            batch.Clear();
            var end = Math.Min(at + BatchSize, to);
            for (var i = at; i < end; i++)
                batch.Add(tokens[i], offset + i, LLamaSeqId.Zero, logitsAtEnd && i == to - 1);
            await DecodeAsync(batch);
        }
        return batch;
    }

    private async Task DecodeAsync(LLamaBatch batch)
    {
        var result = await _context.DecodeAsync(batch);
        if (result != DecodeResult.Ok)
            throw new InvalidOperationException($"il modello non ha elaborato il testo: {result}");
    }
}
