namespace CombatSolver;

// This is a transparent deck-building heuristic, not a prediction of run win rate.
// Inputs contain values only. Neither recommendations nor portfolio search touch live models.
internal sealed record RunCardProfile(string Id, string Name, double Cost, double Attack,
    double Block, double Draw, double Energy, double Scaling, double ExhaustPayoff,
    double ExhaustSupply, double StrengthPayoff, double Weak, double Vulnerable,
    bool Curse, bool Basic, bool Covered, bool Removable = true);

internal sealed record RunPlanningContext(RunCardProfile[] Deck, string[] Relics,
    int Hp, int MaxHp, int Gold, int Act, int Floor, int EmptyPotionSlots);

internal sealed record RunDecisionCandidate(string Key, string Title, int Price = 0,
    RunCardProfile? AddCard = null, int RemoveIndex = -1, int Heal = 0, int MaxHp = 0,
    int GoldGain = 0, double OtherValue = 0, bool Covered = true, string Detail = "");

internal sealed record RunDecisionScore(RunDecisionCandidate Candidate, double Score,
    double DeckDelta, double HealthDelta, double EconomyDelta);

internal static class RunDecisionPolicy
{
    public static double DeckValue(IReadOnlyList<RunCardProfile> deck, IReadOnlyCollection<string> relics)
    {
        if (deck.Count == 0) return -100;
        double n = deck.Count;
        double turns = 5 / n;
        double attacks = deck.Sum(c => c.Attack / Math.Max(1, c.Cost));
        double blocks = deck.Sum(c => c.Block / Math.Max(1, c.Cost));
        double draw = deck.Sum(c => c.Draw);
        double energy = deck.Sum(c => c.Energy);
        double scaling = deck.Sum(c => c.Scaling);
        double costs = deck.Where(c => !c.Curse).Sum(c => c.Cost) / Math.Max(1, deck.Count(c => !c.Curse));
        double exhaustSupply = deck.Sum(c => c.ExhaustSupply);
        double exhaustPayoff = deck.Sum(c => c.ExhaustPayoff);
        double strengthPayoff = deck.Sum(c => c.StrengthPayoff);
        if (relics.Contains("VAJRA")) scaling += 1;
        if (relics.Contains("BAG_OF_PREPARATION")) draw += 0.5;
        if (relics.Contains("ORICHALCUM")) blocks += 3;
        double value = 16 * Math.Log(1 + attacks * turns / 7)
            + 15 * Math.Log(1 + blocks * turns / 7)
            + 7 * Math.Log(1 + draw * turns)
            + 8 * Math.Log(1 + energy * turns)
            + 10 * Math.Log(1 + scaling * turns)
            + 5 * Math.Log(1 + deck.Sum(c => c.Weak) * turns)
            + 4 * Math.Log(1 + deck.Sum(c => c.Vulnerable) * turns)
            + 7 * Math.Log(1 + exhaustSupply * exhaustPayoff * turns / Math.Max(1, n / 5))
            + 4 * Math.Log(1 + strengthPayoff * scaling * turns)
            - 5 * deck.Where(c => c.Curse).Sum(c => c.Removable ? 1 : 1.8) * turns
            - Math.Max(0, costs - (1.2 + energy * turns / 5)) * 5;
        // An extra copy must justify its dilution of all existing roles.
        return value;
    }

    public static RunDecisionScore Evaluate(RunPlanningContext context, RunDecisionCandidate candidate)
    {
        List<RunCardProfile> deck = [.. context.Deck];
        if (candidate.RemoveIndex >= 0)
        {
            if (candidate.RemoveIndex >= deck.Count) throw new ArgumentOutOfRangeException(nameof(candidate.RemoveIndex));
            deck.RemoveAt(candidate.RemoveIndex);
        }
        if (candidate.AddCard != null) deck.Add(candidate.AddCard);
        double deckDelta = DeckValue(deck, context.Relics) - DeckValue(context.Deck, context.Relics);
        int maxHp = context.MaxHp + candidate.MaxHp;
        int hp = Math.Min(maxHp, context.Hp + candidate.Heal);
        double urgency = 0.25 + 2 * Math.Pow(1 - context.Hp / (double)Math.Max(1, context.MaxHp), 2);
        double health = (hp - context.Hp) * urgency + candidate.MaxHp * 0.35;
        if (hp <= 0 || maxHp <= 0) health = -10000;
        int goldDelta = candidate.GoldGain - candidate.Price;
        // Spending the last gold is more expensive than spending a surplus. The same
        // function is used for rewards, shops, and event trades.
        double GoldValue(int gold) => 2.5 * Math.Log(1 + Math.Max(0, gold) / 60d);
        double economy = GoldValue(context.Gold + goldDelta) - GoldValue(context.Gold);
        return new(candidate, deckDelta + health + economy + candidate.OtherValue,
            deckDelta, health, economy);
    }

    public static RunDecisionScore[] Rank(RunPlanningContext context, IEnumerable<RunDecisionCandidate> candidates)
        => candidates.Where(c => c.Price <= context.Gold && c.Price >= 0)
            .Select(c => Evaluate(context, c)).OrderByDescending(s => s.Score)
            .ThenBy(s => s.Candidate.Price).ThenBy(s => s.Candidate.Key, StringComparer.Ordinal).ToArray();

    // Exact subset search over the *visible* shop, including keeping all the gold.
    // Candidate cards are scored jointly, rather than adding independent tier scores.
    public static RunDecisionCandidate[] ShopPortfolio(RunPlanningContext context, IReadOnlyList<RunDecisionCandidate> offers)
    {
        if (offers.Count > 20) throw new InvalidOperationException("Shop portfolio exceeds the bounded visible inventory.");
        RunDecisionCandidate[] best = [];
        double bestScore = 0;
        List<RunDecisionCandidate> chosen = [];
        void Search(int index, int price, int removed)
        {
            RunCardProfile[] deck = context.Deck.Where((_, i) => i != removed)
                .Concat(chosen.Where(c => c.AddCard != null).Select(c => c.AddCard!)).ToArray();
            double score = DeckValue(deck, context.Relics) - DeckValue(context.Deck, context.Relics)
                + chosen.Sum(c => c.OtherValue)
                + 2.5 * Math.Log((60d + context.Gold - price) / (60d + context.Gold));
            if (score > bestScore + 0.0001)
            {
                bestScore = score;
                best = [.. chosen];
            }
            for (int i = index; i < offers.Count; i++)
            {
                RunDecisionCandidate offer = offers[i];
                if (!offer.Covered || price + offer.Price > context.Gold
                    || offer.RemoveIndex >= 0 && removed >= 0) continue;
                chosen.Add(offer);
                Search(i + 1, price + offer.Price, offer.RemoveIndex >= 0 ? offer.RemoveIndex : removed);
                chosen.RemoveAt(chosen.Count - 1);
            }
        }
        Search(0, 0, -1);
        return best;
    }
}
