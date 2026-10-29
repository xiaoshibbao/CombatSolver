using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Localization.DynamicVars;

namespace CombatSolver;

internal static class RunDecisionCapture
{
    // Coverage means that these features describe the card's principal role. It
    // does not claim the heuristic models every combat interaction.
    private static readonly HashSet<string> CoveredCards = new(StringComparer.Ordinal)
    {
        "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "SHRUG_IT_OFF", "POMMEL_STRIKE",
        "TWIN_STRIKE", "CLEAVE", "IRON_WAVE", "TRUE_GRIT",
        "INFLAME", "DEMON_FORM", "DARK_EMBRACE", "FEEL_NO_PAIN", "SECOND_WIND", "BURNING_PACT",
        "FINESSE", "FLASH_OF_STEEL", "SWIFT_STRIKE", "BLIND", "TRIP",
        "STRIKE_SILENT", "DEFEND_SILENT", "NEUTRALIZE", "BACKFLIP", "FOOTWORK", "DAGGER_THROW",
        "STRIKE_DEFECT", "DEFEND_DEFECT",
    };

    public static RunPlanningContext Context(Player player)
        => new(player.Deck.Cards.Select(Card).ToArray(), player.Relics.Select(r => r.Id.Entry).ToArray(),
            (int)player.Creature.CurrentHp, (int)player.Creature.MaxHp, player.Gold,
            player.RunState.CurrentActIndex, player.RunState.TotalFloor,
            player.PotionSlots.Count(p => p == null));

    public static double Value(DynamicVarSet vars, string key)
        => vars.TryGetValue(key, out DynamicVar? value) ? Math.Max(0, (double)value.BaseValue) : 0;

    public static RunCardProfile Card(CardModel card)
    {
        string id = card.Id.Entry;
        double V(string key) => Value(card.DynamicVars, key);
        bool curse = card.Type == CardType.Curse || card.Type == CardType.Status;
        bool basic = card.Rarity == CardRarity.Basic;
        double damage = V("Damage");
        double block = V("Block");
        double draw = V("Cards");
        double energy = V("Energy");
        double scaling = V("Strength") + V("Dexterity") + V("Focus") * 2;
        double exhaustPayoff = id is "DARK_EMBRACE" or "FEEL_NO_PAIN" ? 2 : 0;
        double exhaustSupply = id is "SECOND_WIND" ? 3 : id is "TRUE_GRIT" or "BURNING_PACT" ? 1 : 0;
        double strengthPayoff = id is "TWIN_STRIKE" ? 2 : damage > 0 ? 1 : 0;
        if (id == "TWIN_STRIKE") damage *= 2;
        if (card.TargetType == TargetType.AllEnemies) damage *= 1.6;
        if (id is "INFLAME" or "DEMON_FORM" && scaling == 0) scaling = card.CurrentUpgradeLevel > 0 ? 3 : 2;
        if (id == "FOOTWORK" && scaling == 0) scaling = card.CurrentUpgradeLevel > 0 ? 3 : 2;
        if (id == "BLOODLETTING") energy = Math.Max(energy, card.CurrentUpgradeLevel > 0 ? 3 : 2);
        // Unknown features may be displayed as partial evidence, but never qualify
        // the option for automatic execution.
        bool covered = (curse || CoveredCards.Contains(id))
            && card.GetType().Assembly == typeof(CardModel).Assembly
            && card.Enchantment == null && card.Affliction == null;
        return new(id, card.Title, Math.Max(0, card.EnergyCost.Canonical), damage, block,
            draw, energy, scaling, exhaustPayoff, exhaustSupply, strengthPayoff,
            V("Weak"), V("Vulnerable"), curse, basic, covered, card.IsRemovable);
    }

    public static (double Value, bool Covered) Relic(RelicModel relic, RunPlanningContext context)
        => relic.GetType().Assembly != typeof(RelicModel).Assembly ? (0, false) : relic.Id.Entry switch
        {
            "VAJRA" => (context.Deck.Sum(c => c.StrengthPayoff) * 0.18, true),
            "BAG_OF_PREPARATION" => (2.1, true),
            "ANCHOR" => (2.5, true),
            "ORICHALCUM" => (context.Deck.Sum(c => c.Block) < 25 ? 3 : 1, true),
            "PEN_NIB" => (context.Deck.Count(c => c.Attack > 0) * 0.18, true),
            "STRIKE_DUMMY" => (context.Deck.Count(c => c.Id.StartsWith("STRIKE_", StringComparison.Ordinal)) * 0.5, true),
            "BLOOD_VIAL" => (context.Hp < context.MaxHp / 2 ? 2.5 : 1.2, true),
            "HAPPY_FLOWER" => (3, true),
            "ODDLY_SMOOTH_STONE" => (context.Deck.Count(c => c.Block > 0) * 0.25, true),
            _ => (0, false),
        };
}
