using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Assets;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRunDecisionRoomsAsync(Player player)
    {
        SolverSettingsData original = SolverSettings.Current;
        SolverSettings.ApplyForTesting(original with { AutomaticCalculationEnabled = false, RunDecisionsAutomatic = false });
        try
        {
            player.Gold = 500;
            CardModel removalTarget = player.Deck.Cards.First(c => c.Id.Entry == "STRIKE_IRONCLAD" && c.IsRemovable);
            // Exercise the native inventory purchase contract directly. Rendering
            // the full merchant room in Godot's dummy renderer crashes its font
            // texture loader before our recommendation code is reached.
            MerchantInventory shop = MerchantInventory.CreateForNormalMerchant(player);
            MerchantCardRemovalEntry removal = shop.CardRemovalEntry
                ?? throw new InvalidOperationException("Native shop fixture has no removal service.");
            var before = RunDecisionCapture.Context(player);
            int gold = player.Gold;
            int cards = player.Deck.Cards.Count;
            RunDecisionPolicy.Rank(before, [new("remove", "remove", removal.Cost,
                RemoveIndex: Array.IndexOf(player.Deck.Cards.ToArray(), removalTarget))]);
            if (player.Gold != gold || player.Deck.Cards.Count != cards)
                throw new InvalidOperationException("Shop recommendation mutated native state.");
            int cost = removal.Cost;
            await RunDecisionController.PurchaseEntryAsync(removal, shop, removalTarget);
            if (player.Gold != gold - cost || player.Deck.Cards.Contains(removalTarget) || player.Deck.Cards.Count != cards - 1)
                throw new InvalidOperationException("Native shop removal did not spend exactly the price and remove exactly the planned card.");
            _completedChecks.Add("RunDecisions:NativeShopEntry:NoMutationDuringRecommendation:ExactGoldCost:ExactRemovalInstance");

            await RunManager.Instance.EnterRoom(new EventRoom(ModelDb.Event<SunkenTreasury>()));
            for (int frame = 0; frame < 10; frame++) await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
            var room = (EventRoom)((RunState)player.RunState).CurrentRoom!;
            RunDecisionSession choice = RunDecisionController.Capture()
                ?? throw new InvalidOperationException("Native event was not captured.");
            gold = player.Gold;
            cards = player.Deck.Cards.Count;
            if (!choice.View.CanExecute || choice.Execute == null)
                throw new InvalidOperationException("Supported scalar event was not executable.");
            if (RunDecisionController.Capture()?.Stamp != choice.Stamp || player.Gold != gold)
                throw new InvalidOperationException("Event recommendation mutated native state.");
            if (!choice.View.CanAuto) throw new InvalidOperationException("Supported single-player event did not permit opt-in automation.");
            SolverSettings.ApplyForTesting(SolverSettings.Current with { RunDecisionsAutomatic = true });
            while (!room.LocalMutableEvent.IsFinished)
                await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
            int expected = room.LocalMutableEvent.DynamicVars["SmallChestGold"].IntValue;
            if (player.Gold != gold + expected || player.Deck.Cards.Count != cards)
                throw new InvalidOperationException("Native event did not apply the recommended safe chest's exact modeled resources.");
            _completedChecks.Add("RunDecisions:NativeEvent:NoMutationDuringRecommendation:OptInAuto:ExactGold:NoUnplannedCurse");
        }
        finally { SolverSettings.ApplyForTesting(original); }
    }

    private async Task AssertRunDecisionsAsync(Player player)
    {
        static void Check(bool valid, string reason)
        {
            if (!valid) throw new InvalidOperationException("Run decisions: " + reason);
        }
        RunCardProfile attack = new("attack", "attack", 1, 12, 0, 0, 0, 0, 0, 0, 1, 0, 0, false, false, true);
        RunCardProfile block = attack with { Id = "block", Attack = 0, Block = 12, StrengthPayoff = 0 };
        RunPlanningContext context = new(Enumerable.Repeat(attack, 8).Concat(Enumerable.Repeat(block, 2)).ToArray(),
            [], 60, 80, 120, 0, 6, 1);
        RunDecisionCandidate takeAttack = new("attack", "attack", AddCard: attack);
        RunDecisionCandidate takeBlock = new("block", "block", AddCard: block);
        Check(RunDecisionPolicy.Rank(context, [takeAttack, takeBlock])[0].Candidate == takeBlock,
            "attack-heavy deck must value a missing defensive role");
        var defenseHeavy = context with { Deck = Enumerable.Repeat(block, 8).Concat(Enumerable.Repeat(attack, 2)).ToArray() };
        Check(RunDecisionPolicy.Rank(defenseHeavy, [takeAttack, takeBlock])[0].Candidate == takeAttack,
            "recommendation must change with the actual deck");
        var weak = attack with { Attack = 1 };
        Check(RunDecisionPolicy.Rank(context, [new("skip", "skip"), new("weak", "weak", AddCard: weak)])[0].Candidate.Key == "skip",
            "skip must beat dilution by a weak redundant card");
        RunDecisionCandidate[] offers = [new("one", "one", 100, OtherValue: 5),
            new("two", "two", 60, OtherValue: 3), new("three", "three", 60, OtherValue: 3),
            new("unknown", "unknown", 1, OtherValue: 100, Covered: false)];
        var portfolio = RunDecisionPolicy.ShopPortfolio(context, offers);
        Check(portfolio.Select(c => c.Key).Order().SequenceEqual(new[] { "three", "two" })
            && portfolio.Sum(c => c.Price) <= context.Gold, "shop must optimize the affordable joint portfolio");
        var curse = attack with { Id = "curse", Attack = 0, StrengthPayoff = 0, Curse = true };
        var cursed = context with { Deck = [.. context.Deck, curse] };
        Check(RunDecisionPolicy.Evaluate(context, new("curse", "curse", AddCard: curse)).Score
            > RunDecisionPolicy.Evaluate(context, new("permanent-curse", "permanent curse", AddCard: curse with { Removable = false })).Score,
            "an unremovable curse must carry a larger long-term cost");
        Check(RunDecisionPolicy.Rank(cursed, [new("remove-attack", "remove attack", RemoveIndex: 0),
            new("remove-curse", "remove curse", RemoveIndex: cursed.Deck.Length - 1)])[0].Candidate.Key == "remove-curse",
            "removal must compare actual deck deltas");
        var wounded = context with { Hp = 5 };
        Check(RunDecisionPolicy.Evaluate(wounded, new("heal", "heal", Heal: 10)).Score
            > RunDecisionPolicy.Evaluate(context with { Hp = 80 }, new("heal", "heal", Heal: 10)).Score,
            "healing must use missing HP and urgency");
        Check(RunDecisionPolicy.Evaluate(wounded, new("lethal", "lethal", Heal: -10)).Score < -1000,
            "lethal event cost must not be offset by ordinary rewards");
        Check(!new SolverSettingsData().RunDecisionsAutomatic
            && SolverSettings.RoundTripForTesting(new() { RunDecisionsAutomatic = true }).RunDecisionsAutomatic,
            "recommendation-only default and persisted opt-in");
        _completedChecks.Add("RunDecisions:ContextualRoles:Skip:Dilution:BudgetedPortfolio:UnknownExcluded:CurseRemoval:HpUrgency:Lethal:OptInPersistence");

        SolverSettingsData original = SolverSettings.Current;
        SolverSettings.ApplyForTesting(original with { AutomaticCalculationEnabled = false, RunDecisionsAutomatic = false });
        CardModel card = player.RunState.CreateCard(ModelDb.Card<ShrugItOff>(), player);
        NCardRewardSelectionScreen screen = NCardRewardSelectionScreen.ShowScreen(
            [new CardCreationResult(card)], [new CardRewardAlternative("Skip", PostAlternateCardRewardAction.EndSelectionAndCompleteReward)])
            ?? throw new InvalidOperationException("Native reward screen could not be created.");
        try
        {
            for (int frame = 0; frame < 8; frame++) await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
            var combat = (MegaCrit.Sts2.Core.Combat.CombatState)player.Creature.CombatState!;
            var before = ContinuationStamp.CaptureLive(combat);
            RunDecisionSession first = RunDecisionController.Capture()
                ?? throw new InvalidOperationException("Native reward was not captured.");
            RunDecisionSession second = RunDecisionController.Capture()!;
            Check(first.Stamp == second.Stamp && first.View.Rows.Length == 2,
                "stable native card/skip identity");
            int oldGold = player.Gold;
            player.Gold = oldGold + 1;
            Check(RunDecisionController.Capture()?.Stamp != first.Stamp,
                "a changed native resource must invalidate the old recommendation stamp");
            player.Gold = oldGold;
            Check(before.StateText == ContinuationStamp.CaptureLive(combat).StateText,
                "recommendation must not mutate native state or RNG");
            Task<int?> choice = screen.OptionSelected();
            Check(!choice.IsCompleted, "recommendation-only must not select a reward");
            Check(first.Execute != null, "supported reward must be executable");
            await first.Execute!();
            Check(choice.IsCompletedSuccessfully && choice.Result is >= 0 and <= 1,
                "execution must resolve the actual native selection task");
            _completedChecks.Add("RunDecisions:NativeReward:CardAndSkip:NoMutation:NoAutoByDefault:NativeSelectionTaskResolved");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(screen) && screen.IsInsideTree()) NOverlayStack.Instance!.Remove(screen);
            SolverSettings.ApplyForTesting(original);
        }
    }
}
