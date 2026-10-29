using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed record MultiplayerSupportInsertion(SearchNode Node, RouteAnnotations Annotations);

    private MultiplayerSupportInsertion? TryAppendPureSupport(
        SearchNode original,
        RouteAnnotations originalAnnotations,
        Stopwatch requestClock)
    {
        if (_includeTurnSetup)
            return null;

        PlanAction[] actions = original.Actions.ToArray();
        int insertAt = Array.FindIndex(actions, action => action.Turn == _startTurnNumber
            && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn));
        if (insertAt < 0)
            return null;

        SimulationSnapshot prefix = Replay(actions.Take(insertAt).ToArray());
        try
        {
            CombatPredictionSimulator simulator = prefix.Simulator;
            IReadOnlyList<PredictedCard> hand = simulator.State.GetPlayerCombatState(_player).Hand.Cards;
            for (int handIndex = 0; handIndex < hand.Count; handIndex++)
            {
                ReplayCancellationToken.ThrowIfCancellationRequested();
                PredictedCard card = hand[handIndex];
                if (!simulator.CanPlay(card))
                    continue;
                foreach ((int targetIndex, Creature? target) in TargetsFor(card, simulator))
                {
                    bool pureSupport = IsPureTeammateSupport(card, target);
                    if (!card.Original.IsValidTarget(target))
                        continue;
                    string stateKey = CardChoiceSupport.ChoiceCardKey(card);
                    PlanAction support = new(
                        PlanActionKind.PlayCard,
                        _startTurnNumber,
                        card.Preview.Id.Entry,
                        hand.Take(handIndex).Count(prior => prior.Preview.Id == card.Preview.Id),
                        targetIndex,
                        target?.CombatId,
                        displayNames.Card(card.Preview),
                        displayNames.Creature(target,
                            ((SimulatedCombatState)simulator.State.CombatState).KnownEnemies),
                        CardStateKey: stateKey,
                        CardStateOccurrence: hand.Take(handIndex).Count(prior =>
                            CardChoiceSupport.ChoiceCardKey(prior) == stateKey),
                        CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? "",
                        CardUpgradeLevel: card.Preview.CurrentUpgradeLevel);
                    // A bounded finalization pass may spend otherwise unused resources, but
                    // never guesses choices or advances any shared random stream after timeout.
                    bool deterministic = IsSafeDeterministicTail(prefix, actions.Take(insertAt), support, pureSupport);
                    if (!deterministic && (!pureSupport
                        || requestClock.ElapsedMilliseconds >= _profile.SoftTimeBudgetMilliseconds))
                        continue;
                    bool teammateChoice = card.Preview is Tutor;
                    if (teammateChoice
                        && card.GetEnergyCostWithModifiers(simulator,
                            simulator.State.GetPlayerCombatState(_player)) > 0
                        && !PersistentRelicSupport.ShouldPlayerResetEnergy(
                            (SimulatedCombatState)simulator.State.CombatState, _player))
                        continue;
                    PlanAction[] insertedActions = teammateChoice
                        ? [.. actions.Take(insertAt), support]
                        : [.. actions.Take(insertAt), support, .. actions.Skip(insertAt)];
                    SearchNode? inserted = ReplayAdjustedRoute(insertedActions,
                        original.GetTurnSetupChoices(), original.GetTurnSetupPlayState(), originalAnnotations);
                    if (inserted == null)
                        continue;
                    bool accepted = teammateChoice
                        ? inserted.Snapshot.BoundaryReason == SearchBoundaryReason.PendingChoice
                          && !inserted.Snapshot.PlayerDead
                          && inserted.Snapshot.PlayerHp >= prefix.PlayerHp
                          && inserted.Snapshot.CumulativePlayerHpLost <= prefix.CumulativePlayerHpLost
                          && inserted.Snapshot.Stars >= prefix.Stars
                        : !inserted.Snapshot.PlayerDead
                        && inserted.Snapshot.ProjectedPlayerHp >= original.Snapshot.ProjectedPlayerHp
                        && inserted.Snapshot.PlayerHp >= original.Snapshot.PlayerHp
                        && inserted.Snapshot.PlayerMaxHp >= original.Snapshot.PlayerMaxHp
                        && inserted.Snapshot.LongTermResourceValue >= original.Snapshot.LongTermResourceValue
                        && inserted.FutureSoldHp <= original.FutureSoldHp
                        && inserted.PotionCount <= original.PotionCount
                        && inserted.Snapshot.CumulativePlayerHpLost <= original.Snapshot.CumulativePlayerHpLost
                        && inserted.Snapshot.Energy >= original.Snapshot.Energy
                        && inserted.Snapshot.EnemyHp <= original.Snapshot.EnemyHp
                        && inserted.Snapshot.BoundaryReason == original.Snapshot.BoundaryReason;
                    if (!accepted)
                    {
                        inserted.Snapshot.ReleaseSimulator();
                        continue;
                    }
                    RouteAnnotations annotations = BuildRouteAnnotations(inserted);
                    policy.Diagnostics.Info($"[CombatSolver/Test] MULTIPLAYER_SUPPORT_INSERTED "
                        + $"card={support.CardId} target={support.TargetCombatId} turn={support.Turn}");
                    return new MultiplayerSupportInsertion(inserted, annotations);
                }
            }
            return null;
        }
        finally
        {
            prefix.ReleaseSimulator();
        }
    }

    private bool IsSafeDeterministicTail(SimulationSnapshot before,
        IEnumerable<PlanAction> prefix, PlanAction action, bool pureSupport)
    {
        SimulationSnapshot after = Replay([.. prefix, action]);
        try
        {
            if (after.HasRisk || after.BoundaryReason != SearchBoundaryReason.None
                || after.Turn != before.Turn || after.PlayerDead
                || after.PlayerHp < before.PlayerHp || after.PlayerMaxHp < before.PlayerMaxHp
                || after.PlayerBlock < before.PlayerBlock
                || after.CumulativePlayerHpLost > before.CumulativePlayerHpLost
                || after.LongTermResourceValue < before.LongTermResourceValue)
                return false;
            if (!pureSupport && after.EnemyHp >= before.EnemyHp && after.PlayerBlock <= before.PlayerBlock)
                return false;
            return ContinuationStamp.CapturePredicted(_player, before.Simulator, before.Turn,
                    _forecast, _startTurnNumber).RngStateText
                == ContinuationStamp.CapturePredicted(_player, after.Simulator, after.Turn,
                    _forecast, _startTurnNumber).RngStateText;
        }
        finally { after.ReleaseSimulator(); }
    }

    private MultiplayerSupportInsertion? TryCompleteMultiplayerTurn(
        SearchNode original, RouteAnnotations annotations)
    {
        if (_includeTurnSetup || original.Snapshot.HasRisk
            || original.Snapshot.PlayerDead || original.Snapshot.AllEnemiesDead
            || original.Snapshot.BoundaryReason is SearchBoundaryReason.PendingChoice
                or SearchBoundaryReason.UnsupportedEffect or SearchBoundaryReason.DynamicResolution)
            return null;
        PlanAction[] actions = original.Actions.ToArray();
        if (actions.Any(action => action.Turn == _startTurnNumber
            && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn)))
            return null;
        if (actions.Any(action => action.Turn != _startTurnNumber))
            return null;
        SearchNode? completed = ReplayAdjustedRoute(
            [.. actions, new PlanAction(PlanActionKind.EndTurn, _startTurnNumber)],
            original.GetTurnSetupChoices(), original.GetTurnSetupPlayState(), annotations);
        if (completed == null)
            return null;
        if (completed.Snapshot.HasRisk || completed.Snapshot.BoundaryReason != SearchBoundaryReason.None)
        {
            completed.Snapshot.ReleaseSimulator();
            return null;
        }
        return new(completed, BuildRouteAnnotations(completed));
    }

    internal (IReadOnlyList<PlanAction> Actions, ContinuationStamp State) VerifyTurnFinalizationForTesting()
    {
        SimulationSnapshot snapshot = Replay([]);
        SearchNode node = new(null, 0, snapshot.PotionUseCount, snapshot.PotionStrategicCost,
            snapshot.Turn, SearchRouteTraits.None, 0, snapshot.Score, snapshot.StateKey,
            snapshot.HasRisk, snapshot.BoundaryReason, false, null, snapshot,
            CombatProgressState.Capture(snapshot));
        try
        {
            RouteAnnotations annotations = BuildRouteAnnotations(node);
            MultiplayerSupportInsertion completed = TryCompleteMultiplayerTurn(node, annotations)
                ?? throw new InvalidOperationException("Stable empty multiplayer prefix did not acquire an end-turn boundary.");
            node.Snapshot.ReleaseSimulator();
            node = completed.Node;
            annotations = completed.Annotations;
            for (int count = 0; count < 16; count++)
            {
                MultiplayerSupportInsertion? tail = TryAppendPureSupport(node, annotations, Stopwatch.StartNew());
                if (tail == null) break;
                node.Snapshot.ReleaseSimulator();
                node = tail.Node;
                annotations = tail.Annotations;
            }
            return (node.Actions.ToArray(), ContinuationStamp.CapturePredicted(
                _player, node.Snapshot.Simulator, node.Snapshot.Turn, _forecast, _startTurnNumber));
        }
        finally { node.Snapshot.ReleaseSimulator(); }
    }

    internal SolverCurrentTurnPreview? ReevaluateMultiplayerCurrentTurn(
        SolverResult original,
        IReadOnlyList<PlanAction>? remainingActions = null)
    {
        if (root.PlayerCount < 2 || original.StartTurnNumber != root.StartTurnNumber)
            throw new InvalidOperationException("Multiplayer route must be replayed in its original local turn.");
        PlanAction[] actions = remainingActions?.ToArray()
            ?? original.BestNode.Actions
                .Where(action => action.Turn == root.StartTurnNumber).ToArray();
        if (actions.Length == 0)
            throw new InvalidOperationException("Multiplayer route has no current-turn actions.");
        RouteAnnotations originalAnnotations = new(
            original.HpLostByTurn, original.HpRecoveredByTurn, original.EnemyHpLostByTurn,
            original.SoldHpByTurn, original.MaxBlockByTurn, original.ActualBlockByTurn,
            original.EnergyLeftByTurn, original.PotionCountByTurn,
            original.PotionStrategicCostByTurn, original.KillsAfterAction,
            original.CombatEndedTurn, original.DeathTurn);
        SearchNode? replayed = ReplayAdjustedRoute(actions, [], null, originalAnnotations);
        if (replayed == null)
            return null;
        try
        {
            bool awaitsTeammateChoice = actions[^1].CardId == ModelDb.Card<Tutor>().Id.Entry
                && replayed.Snapshot.BoundaryReason == SearchBoundaryReason.PendingChoice;
            if (replayed.Snapshot.HasRisk
                || replayed.Snapshot.BoundaryReason != SearchBoundaryReason.None && !awaitsTeammateChoice)
                throw new InvalidOperationException("Multiplayer route replay reached an unmodeled or unresolved boundary.");
            RouteAnnotations updated = BuildRouteAnnotations(replayed);
            int turn = root.StartTurnNumber;
            return new SolverCurrentTurnPreview(
                0,
                turn,
                actions,
                updated.HpLostByTurn.GetValueOrDefault(turn),
                updated.HpRecoveredByTurn.GetValueOrDefault(turn),
                updated.EnemyHpLostByTurn.GetValueOrDefault(turn),
                updated.EnergyLeftByTurn.GetValueOrDefault(turn),
                updated.CombatEndedTurn == turn);
        }
        finally
        {
            replayed.Snapshot.ReleaseSimulator();
        }
    }

    internal string PredictMultiplayerActionRng(PlanAction action)
    {
        if (root.PlayerCount < 2 || action.Turn != root.StartTurnNumber)
            throw new InvalidOperationException("Multiplayer action RNG requires a current-turn multiplayer root.");
        SimulationSnapshot predicted = Replay([action]);
        try
        {
            if (predicted.HasRisk || predicted.BoundaryReason != SearchBoundaryReason.None)
                throw new InvalidOperationException("Multiplayer action RNG reached an unresolved boundary.");
            return ContinuationStamp.CapturePredicted(
                _player, predicted.Simulator, predicted.Turn, root.Forecast,
                root.StartTurnNumber).RngStateText;
        }
        finally
        {
            predicted.ReleaseSimulator();
        }
    }
}
