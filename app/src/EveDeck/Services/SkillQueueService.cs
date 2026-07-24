using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveDeck.Models;

namespace EveDeck.Services;

public enum SkillAlertKind { Completed, QueueEmpty, QueueLow }

// SkillId/FinishedLevel are meaningful only for Completed; pass 0 for QueueEmpty/QueueLow.
public sealed record SkillAlert(SkillAlertKind Kind, long CharacterId, int SkillId, int FinishedLevel);

// Per-character carry-over between polls. Public so tests can construct/inspect it.
public sealed class SkillQueueState
{
    public bool HasBaseline { get; init; }
    public bool WasEmpty { get; init; }
    public bool WasLow { get; init; }

    // snapshot of the previous poll's entries; only SkillId, FinishedLevel, FinishDate matter
    public IReadOnlyList<(int SkillId, int FinishedLevel, DateTimeOffset? FinishDate)> Entries { get; init; }
        = Array.Empty<(int, int, DateTimeOffset?)>();
}

// Detects three skill-queue conditions across polls, per character: a skill finishing (Completed),
// the queue running dry (QueueEmpty), and the queue about to run dry within a configurable lookahead
// (QueueLow). Each condition is edge-triggered -- it fires once on the transition and stays quiet
// until the state resets -- so the caller can drive one toast/log line per real event instead of
// re-alerting on every poll while the condition persists.
public sealed class SkillQueueService
{
    private readonly CharacterInfoService _info;
    private readonly ConcurrentDictionary<long, SkillQueueState> _state = new();

    public SkillQueueService(CharacterInfoService info)
    {
        _info = info;
    }

    // Pure, deterministic, fully unit-testable. No I/O.
    public static (IReadOnlyList<SkillAlert> Alerts, SkillQueueState NewState) Evaluate(
        long characterId,
        IReadOnlyList<EsiSkillQueueEntry> currentQueue,
        SkillQueueState? previousState,
        DateTimeOffset now,
        TimeSpan lowThreshold)
    {
        var alerts = new List<SkillAlert>();

        var pending = currentQueue.Where(e => e.FinishDate.HasValue && e.FinishDate.Value > now).ToList();
        var isEmpty = pending.Count == 0;
        DateTimeOffset? endFinish = isEmpty ? null : pending.Max(e => e.FinishDate!.Value);

        // Completed: only meaningful once we have a baseline to diff against.
        if (previousState is { HasBaseline: true })
        {
            foreach (var prev in previousState.Entries)
            {
                if (!prev.FinishDate.HasValue || prev.FinishDate.Value > now)
                    continue;

                var stillPresent = currentQueue.Any(e =>
                    e.SkillId == prev.SkillId && e.FinishedLevel == prev.FinishedLevel);
                if (!stillPresent)
                    alerts.Add(new SkillAlert(SkillAlertKind.Completed, characterId, prev.SkillId, prev.FinishedLevel));
            }
        }

        // QueueEmpty: edge-triggered, including on the very first poll if already empty.
        if (isEmpty && (previousState is null || !previousState.WasEmpty))
            alerts.Add(new SkillAlert(SkillAlertKind.QueueEmpty, characterId, 0, 0));

        // QueueLow: only when not empty; suppressed once already low until it clears.
        var isLow = !isEmpty && endFinish.HasValue && (endFinish.Value - now) < lowThreshold;
        if (isLow && (previousState is null || !previousState.WasLow))
            alerts.Add(new SkillAlert(SkillAlertKind.QueueLow, characterId, 0, 0));

        var newState = new SkillQueueState
        {
            HasBaseline = true,
            WasEmpty = isEmpty,
            WasLow = isLow,
            Entries = currentQueue.Select(e => (e.SkillId, e.FinishedLevel, e.FinishDate)).ToList(),
        };

        return (alerts, newState);
    }

    // Orchestration: fetch each character's queue (forceRefresh:true), run Evaluate, accumulate all
    // alerts, and update per-character state. Best-effort -- a fetch that throws or returns null for
    // one character is skipped (logged) and must not abort the others.
    public async Task<IReadOnlyList<SkillAlert>> PollAsync(
        IEnumerable<long> characterIds, TimeSpan lowThreshold, Action<string>? log, CancellationToken ct)
    {
        var all = new List<SkillAlert>();
        var now = DateTimeOffset.UtcNow;

        foreach (var characterId in characterIds)
        {
            ct.ThrowIfCancellationRequested();

            List<EsiSkillQueueEntry>? queue;
            try
            {
                queue = await _info.GetSkillQueueAsync(characterId, forceRefresh: true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"SkillQueueService: failed to fetch skill queue for character {characterId}: {ex.Message}");
                continue;
            }

            if (queue is null)
            {
                log?.Invoke($"SkillQueueService: no skill queue available for character {characterId}");
                continue;
            }

            _state.TryGetValue(characterId, out var previous);
            var (alerts, newState) = Evaluate(characterId, queue, previous, now, lowThreshold);
            _state[characterId] = newState;
            all.AddRange(alerts);
        }

        return all;
    }

    // Drop a character's carried state (call when a character is unlinked).
    public void Forget(long characterId)
    {
        _state.TryRemove(characterId, out _);
    }
}
