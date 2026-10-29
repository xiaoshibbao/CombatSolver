using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal static class RunEventDecisionCapture
{
    public static RunDecisionSession? Capture(RunPlanningContext context, EventModel model,
        NEventRoom scene, string state, bool singleplayer)
    {
        var options = model.CurrentOptions;
        if (options.Count == 0) return null;
        List<RunDecisionCandidate> candidates = [];
        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            if (option.IsLocked || option.WasChosen) continue;
            string name = option.Title.GetFormattedText();
            string key = i.ToString();
            string id = option.TextKey.Split('.').Last();
            RunDecisionCandidate candidate = new(key, name, Covered: false);
            int V(string variable) => model.DynamicVars[variable].IntValue;
            // Explicit native effect contracts. Option text is displayed, never
            // parsed as code or used to guess an unregistered effect.
            if (model.Id.Entry == "SUNKEN_TREASURY")
            {
                if (id == "FIRST_CHEST") candidate = new(key, name, GoldGain: V("SmallChestGold"));
                if (id == "SECOND_CHEST") candidate = new(key, name, GoldGain: V("LargeChestGold"),
                    AddCard: RunDecisionCapture.Card(ModelDb.Card<Greed>()));
            }
            else if (model.Id.Entry == "ABYSSAL_BATHS")
            {
                if (id is "IMMERSE" or "LINGER") candidate = new(key, name,
                    MaxHp: V("MaxHp"), Heal: V("MaxHp") - V("Damage"));
                if (id == "ABSTAIN") candidate = new(key, name, Heal: V("Heal"));
                if (id == "EXIT_BATHS") candidate = new(key, name);
            }
            if (candidate.Covered && model.Owner is { } owner && option.WillKillPlayer?.Invoke(owner) == true)
                candidate = candidate with { Heal = -context.MaxHp - context.Hp, Covered = true };
            candidates.Add(candidate);
        }
        var ranked = RunDecisionPolicy.Rank(context, candidates);
        if (ranked.Length == 0) return null;
        bool complete = candidates.All(c => c.Covered);
        string stamp = $"event:{scene.GetInstanceId()}:{model.Id.Entry}:{state}:"
            + string.Join('|', options.Select(o => o.TextKey + ":" + o.IsLocked + ":" + o.Description.GetFormattedText()));
        int selected = int.Parse(ranked[0].Candidate.Key);
        async Task Choose()
        {
            scene.OptionButtonClicked(options[selected], selected);
            long deadline = System.Environment.TickCount64 + 3000;
            while (!options[selected].WasChosen && !model.IsFinished)
            {
                if (System.Environment.TickCount64 >= deadline)
                    throw new InvalidOperationException("Native event did not accept the planned option.");
                var host = MegaCrit.Sts2.Core.Nodes.NGame.Instance!;
                await host.ToSignal(host.GetTree(), Godot.SceneTree.SignalName.ProcessFrame);
            }
        }
        // Shared events use the game's vote UI; never issue a unilateral auto vote.
        return RunDecisionController.Session(stamp, "事件", ranked,
            complete ? ranked[0].Candidate.Title : SolverText.Get("本事件尚无完整效果模型，请手动选择"),
            complete && !model.IsShared ? Choose : null, singleplayer && complete && !model.IsShared,
            model.IsShared ? SolverText.Get("共享事件保留原生投票。") : "");
    }
}
