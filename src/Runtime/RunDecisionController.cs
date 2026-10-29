using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CombatSolver;

internal sealed record RunDecisionRow(string Title, string Detail, double Score, bool Covered);
internal sealed record RunDecisionView(string Kind, RunDecisionRow[] Rows, string Recommendation,
    bool CanExecute, bool CanAuto, string Note);
internal sealed record RunDecisionSession(string Stamp, RunDecisionView View, Func<Task>? Execute);

internal static class RunDecisionController
{
    private static RunDecisionSession? _current;
    private static RunDecisionPanel? _panel;
    private static long _lastPoll;
    private static long _shownAt;
    private static bool _executing;
    private static string? _issuedStamp;
    private static string? _failedStamp;
    private static object? _captureFailedRoom;

    public static void Poll(NGame host)
    {
        long now = System.Environment.TickCount64;
        if (now - _lastPoll < 400 || _executing) return;
        _lastPoll = now;
        if (SolverSettings.Current.SolverDisabled || !RunManager.Instance.IsInProgress
            || CombatManager.Instance.IsInProgress)
        {
            _panel?.Hide();
            _current = null;
            return;
        }
        object? room = RunManager.Instance.DebugOnlyGetState()?.CurrentRoom;
        if (room != null && ReferenceEquals(room, _captureFailedRoom)) return;
        _captureFailedRoom = null;
        RunDecisionSession? session;
        try { session = Capture(); }
        catch (Exception exception)
        {
            _captureFailedRoom = room;
            _current = null;
            _panel ??= RunDecisionPanel.Create(host, Execute, automatic =>
                SolverSettings.Update(SolverSettings.Current with { RunDecisionsAutomatic = automatic }));
            _panel.Show();
            _panel.ShowFailure(exception.Message);
            Entry.Logger.Error($"[CombatSolver/RunDecision] CAPTURE_FAILED exception={exception}");
            return;
        }
        if (session == null)
        {
            _panel?.Hide();
            _current = null;
            return;
        }
        if (_current?.Stamp != session.Stamp)
        {
            _current = session;
            _shownAt = now;
            _failedStamp = null;
            _panel ??= RunDecisionPanel.Create(host, Execute, automatic =>
                SolverSettings.Update(SolverSettings.Current with { RunDecisionsAutomatic = automatic }));
            _panel.Present(session.View, SolverSettings.Current.RunDecisionsAutomatic);
            Entry.Logger.Info($"[CombatSolver/RunDecision] RECOMMEND kind={session.View.Kind} "
                + $"auto_allowed={session.View.CanAuto} choice={session.View.Recommendation}");
        }
        if (SolverSettings.Current.RunDecisionsAutomatic && session.View.CanAuto
            && now - _shownAt >= 1800 && _issuedStamp != session.Stamp && _failedStamp != session.Stamp)
            Execute();
    }

    public static async void Execute() => await ExecuteCurrentAsync();

    internal static async Task ExecuteCurrentAsync()
    {
        if (_executing || _current is not { Execute: not null } expected) return;
        _executing = true;
        try
        {
            RunDecisionSession? fresh = Capture();
            if (fresh?.Stamp != expected.Stamp || !fresh.View.CanExecute)
            {
                _current = null;
                Entry.Logger.Warn("[CombatSolver/RunDecision] EXECUTION_REJECTED reason=state_changed");
                return;
            }
            _issuedStamp = fresh.Stamp;
            _panel?.SetExecuting();
            await fresh.Execute!();
            Entry.Logger.Info($"[CombatSolver/RunDecision] EXECUTED kind={fresh.View.Kind} choice={fresh.View.Recommendation}");
            _current = null;
        }
        catch (Exception exception)
        {
            // Fail this decision session visibly. No repeat-click or fallback choice.
            _failedStamp = expected.Stamp;
            _panel?.ShowFailure(exception.Message);
            Entry.Logger.Error($"[CombatSolver/RunDecision] EXECUTION_FAILED kind={expected.View.Kind} exception={exception}");
        }
        finally { _executing = false; }
    }

    internal static RunDecisionSession? Capture()
    {
        if (RunManager.Instance.DebugOnlyGetState() is not RunState run) return null;
        var player = LocalContext.GetMe(run);
        if (player == null) return null;
        RunPlanningContext context = RunDecisionCapture.Context(player);
        string state = JsonSerializer.Serialize(player.ToSerializable(), JsonSerializationUtility.GetTypeInfo<SerializablePlayer>());
        bool singleplayer = run.Players.Count == 1;
        NOverlayStack? overlays = NOverlayStack.Instance;
        var top = overlays?.ScreenCount > 0 ? overlays.Peek() : null;
        if (top is NCardRewardSelectionScreen reward)
        {
            List<RunDecisionCandidate> candidates = [];
            for (int i = 0; i < reward._options.Count; i++)
            {
                var card = reward._options[i].Card;
                RunCardProfile profile = RunDecisionCapture.Card(card);
                candidates.Add(new($"card:{i}", card.Title, AddCard: profile, Covered: profile.Covered));
            }
            // A reward alternative can grant gold/healing through relics. It must
            // not be silently treated as a free skip.
            for (int i = 0; i < reward._extraOptions.Count; i++)
            {
                var alternative = reward._extraOptions[i];
                bool skip = alternative.OptionId == "Skip" && alternative.OnSelect == null;
                candidates.Add(new($"alternative:{i}", alternative.Title.GetFormattedText(), Covered: skip));
            }
            var ranked = RunDecisionPolicy.Rank(context, candidates);
            if (ranked.Length == 0) return null;
            string key = ranked[0].Candidate.Key;
            string stamp = $"reward:{reward.GetInstanceId()}:{state}:" + JsonSerializer.Serialize(candidates);
            async Task Choose()
            {
                NGame host = NGame.Instance!;
                await host.ToSignal(host.GetTree().CreateTimer(NCardRewardSelectionScreen._noSelectionTimeMsec / 1000d),
                    SceneTreeTimer.SignalName.Timeout);
                if (Capture()?.Stamp != stamp)
                    throw new InvalidOperationException("Reward changed before native selection became available.");
                Task<int?> selected = reward._completionSource?.Task
                    ?? throw new InvalidOperationException("Native reward selection has no waiting caller.");
                if (selected.IsCompleted) throw new InvalidOperationException("Native reward selection was already completed.");
                if (key.StartsWith("card:", StringComparison.Ordinal))
                {
                    int index = int.Parse(key.AsSpan(5));
                    reward.SelectCard(reward.GetCardHolder(reward._options[index].Card));
                }
                else reward.OnAlternateRewardSelected(int.Parse(key.AsSpan(12)));
                await selected.WaitAsync(TimeSpan.FromSeconds(3));
            }
            return Session(stamp, "奖励选牌", ranked, ranked[0].Candidate.Title,
                ranked[0].Candidate.Covered ? Choose : null, singleplayer && candidates.All(c => c.Covered));
        }
        if (top != null) return null;
        if (run.CurrentRoom is MerchantRoom && NMerchantRoom.Instance?.Inventory is { IsOpen: true } shop)
            return Shop(context, shop, state, singleplayer, player.Deck.Cards.ToArray());
        if (run.CurrentRoom is EventRoom room && !room.LocalMutableEvent.IsFinished
            && NEventRoom.Instance is { } scene)
            return RunEventDecisionCapture.Capture(context, room.LocalMutableEvent, scene, state, singleplayer);
        return null;
    }

    private static RunDecisionSession Shop(RunPlanningContext context, NMerchantInventory shop,
        string state, bool singleplayer, CardModel[] liveDeck)
    {
        Dictionary<string, NMerchantSlot> slots = [];
        List<RunDecisionCandidate> candidates = [];
        foreach (NMerchantSlot slot in shop.GetAllSlots())
        {
            MerchantEntry entry = slot.Entry;
            if (!entry.IsStocked) continue;
            string key = slot.GetInstanceId().ToString();
            RunDecisionCandidate? candidate = null;
            if (entry is MerchantCardEntry cardEntry)
            {
                RunCardProfile card = RunDecisionCapture.Card((cardEntry.CreationResult
                    ?? throw new InvalidOperationException("Stocked card has no creation result.")).Card);
                candidate = new(key, card.Name, entry.Cost, AddCard: card, Covered: card.Covered);
            }
            else if (entry is MerchantRelicEntry relicEntry)
            {
                var relic = relicEntry.Model ?? throw new InvalidOperationException("Stocked relic has no model.");
                var value = RunDecisionCapture.Relic(relic, context);
                candidate = new(key, relic.Title.GetFormattedText(), entry.Cost,
                    OtherValue: value.Value, Covered: value.Covered);
            }
            else if (entry is MerchantPotionEntry potionEntry)
            {
                var potion = potionEntry.Model ?? throw new InvalidOperationException("Stocked potion has no model.");
                double heal = RunDecisionCapture.Value(potion.DynamicVars, "Heal");
                double block = RunDecisionCapture.Value(potion.DynamicVars, "Block");
                double damage = RunDecisionCapture.Value(potion.DynamicVars, "Damage");
                bool covered = potion.Id.Entry is "BLOCK_POTION" or "FIRE_POTION" or "EXPLOSIVE_POTION";
                candidate = new(key, potion.Title.GetFormattedText(), entry.Cost,
                    OtherValue: context.EmptyPotionSlots > 0 ? (block + damage + heal) * 0.12 : -100,
                    Covered: covered && context.EmptyPotionSlots > 0);
            }
            else if (entry is MerchantCardRemovalEntry removal)
            {
                var removals = context.Deck.Select((card, i) => new RunDecisionCandidate(key,
                    SolverText.Format($"删牌：{card.Name}"), removal.Cost, RemoveIndex: i,
                    Covered: liveDeck[i].IsRemovable && card.Covered))
                    .Where(c => liveDeck[c.RemoveIndex].IsRemovable);
                candidate = RunDecisionPolicy.Rank(context, removals).FirstOrDefault()?.Candidate;
            }
            if (candidate == null) continue;
            candidates.Add(candidate);
            slots.Add(key, slot);
        }
        string stamp = $"shop:{shop.GetInstanceId()}:{state}:" + JsonSerializer.Serialize(candidates);
        if (_current?.Stamp == stamp) return _current;
        RunDecisionCandidate[] portfolio = RunDecisionPolicy.ShopPortfolio(context, candidates);
        var ranked = RunDecisionPolicy.Rank(context, candidates.Append(new("keep", SolverText.Get("保留金币"))));
        Task KeepGold()
        {
            shop.Close();
            var proceed = NMerchantRoom.Instance!.ProceedButton;
            proceed.EmitSignal(MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl.SignalName.Released, proceed);
            return Task.CompletedTask;
        }
        Func<Task>? execute = portfolio.Length == 0 ? KeepGold : () => PurchaseAsync(
            slots[portfolio[0].Key], portfolio[0].RemoveIndex >= 0 ? liveDeck[portfolio[0].RemoveIndex] : null, stamp);
        string recommendation = portfolio.Length == 0 ? SolverText.Get("保留金币")
            : string.Join(" → ", portfolio.Select(c => c.Title)) + SolverText.Format($"（合计 {portfolio.Sum(c => c.Price)} 金币）");
        return Session(stamp, "商店", ranked, recommendation, execute,
            singleplayer && candidates.Where(c => c.Price <= context.Gold).All(c => c.Covered), SolverText.Get("每次购买后会按新价格、金币和库存重新计算。"));
    }

    public static void Reset()
    {
        _current = null;
        _panel = null;
        _issuedStamp = null;
        _failedStamp = null;
        _captureFailedRoom = null;
    }

    internal static async Task PurchaseAsync(NMerchantSlot slot, CardModel? removalTarget, string expectedStamp)
    {
        NMerchantInventory shop = NMerchantRoom.Instance!.Inventory;
        MerchantInventory inventory = shop.Inventory ?? throw new InvalidOperationException("Native shop inventory is not initialized.");
        shop.BlockInput();
        try { await PurchaseEntryAsync(slot.Entry, inventory, removalTarget, expectedStamp); }
        finally { if (GodotObject.IsInstanceValid(shop)) shop.UnblockInput(); }
    }

    internal static async Task PurchaseEntryAsync(MerchantEntry entry, MerchantInventory inventory,
        CardModel? removalTarget, string? expectedStamp = null)
    {
        Task<bool> purchase = entry is MerchantCardRemovalEntry removal
            ? removal.OnTryPurchaseWrapper(inventory, ignoreCost: false, cancelable: true)
            : entry.OnTryPurchaseWrapper(inventory, ignoreCost: false);
        if (removalTarget == null)
        {
            if (!await purchase) throw new InvalidOperationException("Native purchase was refused.");
            if (expectedStamp != null && Capture()?.Stamp == expectedStamp)
                throw new InvalidOperationException("Native purchase did not change the inventory or player state.");
            return;
        }
        long deadline = System.Environment.TickCount64 + 10000;
        bool selected = false;
        while (!purchase.IsCompleted)
        {
            if (NOverlayStack.Instance is { ScreenCount: > 0 } stack
                && stack.Peek() is NDeckCardSelectScreen screen && !selected)
            {
                CardModel[] matches = screen._cards.Where(card => ReferenceEquals(card, removalTarget)
                    || ReferenceEquals(card.DeckVersion, removalTarget)).ToArray();
                if (screen._prefs.MinSelect > 1 || screen._prefs.MaxSelect != 1
                    || matches.Length != 1 || screen._selectedCards.Count != 0)
                    throw new InvalidOperationException($"Native removal selection mismatch: min={screen._prefs.MinSelect} "
                        + $"max={screen._prefs.MaxSelect} matches={matches.Length} selected={screen._selectedCards.Count}.");
                typeof(NDeckCardSelectScreen).GetMethod("OnCardClicked", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(screen, [matches[0]]);
                if (!screen._completionSource.Task.IsCompleted) screen.ConfirmSelection(null!);
                selected = true;
            }
            if (System.Environment.TickCount64 > deadline)
                throw new InvalidOperationException("Native removal did not finish; complete the visible selection manually.");
            await NGame.Instance!.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        if (!await purchase) throw new InvalidOperationException("Native removal purchase was refused.");
        if (removalTarget.Owner?.Deck.Cards.Contains(removalTarget) == true)
            throw new InvalidOperationException("Native purchase completed without removing the planned card.");
    }

    internal static RunDecisionSession Session(string stamp, string kind, RunDecisionScore[] ranked,
        string recommendation, Func<Task>? execute, bool canAuto, string note = "")
    {
        bool covered = ranked.All(s => s.Candidate.Covered);
        RunDecisionRow[] rows = ranked.Select(s => new RunDecisionRow(s.Candidate.Title,
            SolverText.Format($"牌组 {s.DeckDelta:+0.00;-0.00;0} · 生存 {s.HealthDelta:+0.00;-0.00;0} · 经济 {s.EconomyDelta:+0.00;-0.00;0}")
            + (s.Candidate.OtherValue == 0 ? "" : "\n" + SolverText.Format($"条件收益 {s.Candidate.OtherValue:+0.00;-0.00;0}"))
            + (s.Candidate.Covered ? "" : " · " + SolverText.Get("效果尚未完整覆盖，仅供参考")), s.Score, s.Candidate.Covered)).ToArray();
        return new(stamp, new(SolverText.Get(kind), rows, recommendation, execute != null,
            canAuto && covered && execute != null, SolverText.Get("构筑启发式评分，不代表通关率或精确战损。")
                + (covered ? "" : "\n" + SolverText.Get("存在未覆盖效果，自动执行已暂停。")) + "\n" + note), execute);
    }
}
