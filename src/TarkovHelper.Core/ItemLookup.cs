using TarkovHelper.Core.Models;

namespace TarkovHelper.Core;

// A single "X needs this item" hit, unified across the two places an item
// requirement can come from (a quest objective's Items list, or a hideout
// station level's ItemRequirements) so a caller can show one combined list
// without caring which source it came from.
public class ItemNeed
{
    public required string SourceName { get; init; }
    public required string DetailText { get; init; }
    public required int Count { get; init; }
    public required bool FoundInRaid { get; init; }

    // False for a quest not yet active (not started, or locked behind an
    // unmet prerequisite) or a hideout level more than one step beyond the
    // current level - still a genuine future need worth knowing about
    // before selling an item, just not something usable right this moment.
    public required bool IsAvailableNow { get; init; }
}

public static class ItemLookup
{
    // Objectives whose Items list is larger than this are "any one of
    // these" pools (e.g. sellItem "Sell any items to Prapor" objectives,
    // which legitimately list 3000+ eligible items), not "you specifically
    // need this exact item" requirements - matching against those makes
    // nearly every item in the game falsely appear to need that quest.
    // Real bug: "Building Foundations"/"Key Partner" (sellItem objectives
    // with 3300+ item entries each) showed up for almost every hover
    // lookup. Genuine single/small-variant requirements (findItem,
    // giveItem, and most plantItem objectives) are verified to have at
    // most a handful of entries (real data: 120/126 plantItem objectives
    // with Items have exactly 1, the rest top out at 29) - this threshold
    // sits well above that while sitting far below sellItem's thousands.
    private const int MaxPlausibleItemPoolSize = 50;


    // Text-substring match against objective descriptions - the original,
    // still-used-by-the-search-box lookup. Kept as-is: it's a reasonable
    // first pass for free-text search, distinct from the ID-based lookups
    // below which need an exact item, not a typed query.
    public static List<QuestTask> FindTasksReferencingItem(IEnumerable<QuestTask> tasks, string itemNameQuery)
    {
        if (string.IsNullOrWhiteSpace(itemNameQuery))
        {
            return new List<QuestTask>();
        }

        return tasks
            .Where(t => t.Objectives.Any(o =>
                o.Description.Contains(itemNameQuery, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // Exact match against TaskObjective.Items[].Id - reliable (no text
    // matching involved) since objectives of type giveItem/findItem always
    // carry structured item IDs. Only covers those objective types; a
    // "plantItem" objective (e.g. Chumming's stash steps) names the item in
    // its Description text only, not in a structured Items list, so it
    // won't appear here - that's a real gap in tarkov.dev's own data
    // shape, not something this lookup can work around.
    //
    // Covers every incomplete quest, not just currently-active ones - a
    // "should I keep or sell this" decision needs the full picture,
    // including quests not yet started or still locked behind a
    // prerequisite (IsAvailableNow distinguishes which is which).
    public static List<ItemNeed> FindQuestNeedsForItemId(
        IEnumerable<QuestTask> tasks, string itemId, IReadOnlyDictionary<string, QuestTask> tasksById)
    {
        var needs = new List<ItemNeed>();

        foreach (var task in tasks)
        {
            if (task.IsComplete)
            {
                continue;
            }

            foreach (var objective in task.Objectives)
            {
                if (objective.Items.Count > MaxPlausibleItemPoolSize)
                {
                    continue;
                }

                var item = objective.Items.FirstOrDefault(i => i.Id == itemId);
                if (item is null)
                {
                    continue;
                }

                needs.Add(new ItemNeed
                {
                    SourceName = task.Name,
                    DetailText = objective.Description,
                    Count = objective.Count ?? 1,
                    FoundInRaid = objective.FoundInRaid ?? false,
                    IsAvailableNow = task.IsActive || QuestAvailability.IsAvailable(task, tasksById),
                });
            }
        }

        return needs;
    }

    // Same matching as FindQuestNeedsForItemId, but returns the QuestTasks
    // themselves rather than ItemNeed summaries - for callers (e.g. the
    // quest grid) that want to keep showing their normal per-task columns
    // (trader, map, min level, etc.) rather than the condensed ItemNeed
    // shape. Covers every incomplete quest, not just active ones, for the
    // same "keep or sell" reasoning as FindQuestNeedsForItemId - real bug
    // fixed: the UI was previously wired to FindTasksReferencingItem (text
    // substring against objective descriptions) instead of this exact-ID
    // match, which both missed quests not yet active/started AND broke on
    // simple singular/plural mismatches (e.g. "Car battery" not matching
    // "Find Car batteries in raid").
    public static List<QuestTask> FindTasksNeedingItemId(IEnumerable<QuestTask> tasks, string itemId)
    {
        return tasks
            .Where(t => !t.IsComplete && t.Objectives.Any(o =>
                o.Items.Count <= MaxPlausibleItemPoolSize && o.Items.Any(i => i.Id == itemId)))
            .ToList();
    }

    // Exact match against HideoutItemRequirement.Item.Id, across every
    // level not yet built (not just the next one) - same reasoning as
    // quests above: a "keep or sell" decision needs to know about a need
    // three levels away too, just marked as not available yet.
    public static List<ItemNeed> FindHideoutNeedsForItemId(IEnumerable<HideoutStation> stations, string itemId)
    {
        var needs = new List<ItemNeed>();

        foreach (var station in stations)
        {
            foreach (var level in station.Levels)
            {
                if (level.Level <= station.CurrentLevel)
                {
                    continue;
                }

                foreach (var requirement in level.ItemRequirements)
                {
                    if (requirement.Item.Id != itemId)
                    {
                        continue;
                    }

                    needs.Add(new ItemNeed
                    {
                        SourceName = station.Name,
                        DetailText = $"Level {level.Level}",
                        Count = requirement.Count,
                        FoundInRaid = requirement.FoundInRaid,
                        IsAvailableNow = level.Level == station.CurrentLevel + 1,
                    });
                }
            }
        }

        return needs;
    }

    // Resolves noisy OCR text (e.g. from a screenshotted tooltip) to the
    // single best-matching real item name, or null if nothing is close
    // enough to trust. Deliberately simple (normalized substring/prefix
    // match, not a general string-distance algorithm) since OCR errors on
    // EFT's tooltip font are typically missing/extra characters at word
    // boundaries (spacing, punctuation) rather than scrambled letters -
    // exact Levenshtein distance would be overkill and slower for the
    // ~5000-item catalog this runs against on every hotkey press.
    public static (string ItemId, string ItemName)? ResolveItemNameFuzzy(
        IReadOnlyDictionary<string, string> itemNames, string ocrText) =>
        ResolveBestItemMatch(itemNames, new[] { ocrText });

    // Tries every candidate OCR line (e.g. every line of text found in a
    // screenshot region that isn't precisely positioned on the tooltip -
    // see ItemTooltipReader.ReadLinesNearCursor) and returns whichever
    // single best-scoring match was found across all of them, rather than
    // assuming any one particular line is the item name. No positional
    // information here, so every line is weighted equally by text-match
    // quality alone - prefer ResolveBestItemMatch(itemNames,
    // IEnumerable<(string,float)>) when candidate lines have known
    // distances from the cursor, since pure text-length scoring without
    // any positional signal is exactly what caused a real bug (a long,
    // unrelated item name elsewhere in the capture out-scored the actual
    // hovered item).
    public static (string ItemId, string ItemName)? ResolveBestItemMatch(
        IReadOnlyDictionary<string, string> itemNames, IEnumerable<string> candidateLines) =>
        ResolveBestItemMatch(itemNames, candidateLines.Select(line => (line, DistanceWeight: 0f)));

    // Same as above, but each candidate carries a DistanceWeight in [0, 1]
    // (0 = at the cursor, 1 = at the edge of the capture region) so a line
    // physically far from the cursor needs a much stronger text match to
    // win over a line right at the cursor - real bug fixed: previously,
    // score was pure normalized-string-length overlap with no positional
    // signal at all, so a long, unrelated item name elsewhere in a wide
    // capture region (e.g. "Vaseline balm") could out-score the actual
    // hovered item's shorter real name (e.g. "Light bulb") just because it
    // was a longer string, regardless of which line was actually under
    // the cursor.
    public static (string ItemId, string ItemName)? ResolveBestItemMatch(
        IReadOnlyDictionary<string, string> itemNames, IEnumerable<(string Text, float DistanceWeight)> candidateLines)
    {
        string? bestId = null;
        string? bestName = null;
        double bestScore = 0;

        foreach (var (line, distanceWeight) in candidateLines)
        {
            var normalizedQuery = Normalize(line);
            if (normalizedQuery.Length == 0)
            {
                continue;
            }

            // Proximity multiplier: 1.0 at the cursor, falling steeply
            // (quadratically) to 0.04 at the far edge of the capture - a
            // linear falloff wasn't steep enough in practice: a short,
            // correct match right at the cursor ("Bulb") still lost to a
            // much longer, unrelated match far away ("Vaseline balm")
            // because raw string-length scoring scales faster than a mild
            // proximity penalty. Squaring the distance makes "far away"
            // punishing enough that only a dramatically stronger distant
            // match can still win, while nearby matches are barely
            // penalized at all.
            var clampedDistance = Math.Clamp(distanceWeight, 0f, 1f);
            var proximityMultiplier = 1.0 - 0.96 * (clampedDistance * clampedDistance);

            // Real bug: a word-shaped abbreviation match (see IsWordMatch)
            // can't tell a genuine EFT abbreviation ("Hose" for "Corrugated
            // hose", matching essentially one item) apart from a generic
            // category word that's ALSO the last word of many names
            // ("Magazine", matching hundreds) - both are "exact last word"
            // matches by shape alone. Counting how many catalog items this
            // exact query would abbreviation-match, up front, lets the
            // scoring loop below reject the ambiguous case (too many
            // matches = not a real, specific abbreviation) while still
            // accepting genuinely unique/rare ones.
            const int MaxAbbreviationMatches = 3;
            var abbreviationMatchCount = itemNames.Values.Count(candidateName =>
                IsWordMatch(candidateName, normalizedQuery));
            var abbreviationIsSpecificEnough = abbreviationMatchCount is > 0 and <= MaxAbbreviationMatches;

            foreach (var (id, name) in itemNames)
            {
                var normalizedName = Normalize(name);
                if (normalizedName.Length == 0)
                {
                    continue;
                }

                // Real bug (found via a real capture): EFT's own
                // stash-grid caption labels are genuinely ABBREVIATED
                // short names, not the item's full catalog name and not
                // an OCR error - confirmed directly where the game itself
                // displayed "Hose" for "Corrugated hose" and "MScissors"
                // for "Metal cutting scissors" (first-word-initial +
                // last-word, with every middle word dropped - so the
                // abbreviation isn't even a plain substring of the full
                // name, e.g. "mscissors" is NOT contained in
                // "metalcuttingscissors" because "cutting" sits between
                // them). Checked as its own branch, before the
                // Contains-based check below, specifically because it
                // must not depend on substring containment.
                var isAbbreviation = normalizedQuery.Length < normalizedName.Length
                    && abbreviationIsSpecificEnough
                    && IsWordMatch(name, normalizedQuery);

                int rawScore;
                if (normalizedName == normalizedQuery)
                {
                    rawScore = normalizedName.Length * 2;
                }
                else if (isAbbreviation)
                {
                    rawScore = normalizedQuery.Length;
                }
                else if (normalizedName.Contains(normalizedQuery) || normalizedQuery.Contains(normalizedName))
                {
                    // Real bug (original): a short, generic word
                    // ("Magazine") is a substring of hundreds of real item
                    // names ("AK-74 5.45x39 6L23 30-round magazine"), so it
                    // used to clear a flat score floor easily and get
                    // confidently reported as if it were the actual item.
                    // Requiring the OCR text to cover most of whichever
                    // string is shorter (query or name) means a short
                    // fragment can only match a similarly short real name,
                    // not any name that happens to contain it as one word
                    // among many. (The word-shaped abbreviation case above
                    // has its own, separate uniqueness safeguard instead of
                    // this length-ratio one - see abbreviationIsSpecificEnough.)
                    var overlapLength = Math.Min(normalizedName.Length, normalizedQuery.Length);
                    var longerLength = Math.Max(normalizedName.Length, normalizedQuery.Length);
                    if (overlapLength < longerLength * 0.7)
                    {
                        continue;
                    }

                    rawScore = overlapLength;
                }
                else
                {
                    continue;
                }

                var weightedScore = rawScore * proximityMultiplier;
                if (weightedScore > bestScore)
                {
                    bestScore = weightedScore;
                    bestId = id;
                    bestName = name;
                }
            }
        }

        // Require a reasonably substantial overlap, not just a shared short
        // word, to avoid confidently matching garbage OCR output to an
        // unrelated item. Threshold compared against the same scale as
        // rawScore (pre-proximity), so a distant match still needs to
        // clear a real bar, not just be "the least bad option far away."
        if (bestId is null || bestScore < 4)
        {
            return null;
        }

        return (bestId, bestName!);
    }

    private static string Normalize(string text) =>
        new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // True if the normalized query matches one of the specific shapes
    // EFT's real in-grid caption abbreviations were confirmed to take,
    // checked directly against real captures:
    //   - the item's exact last word ("Hose" for "Corrugated hose",
    //     "Peas" for "Can of green peas")
    //   - the item's exact first word ("Diary" for "Slim diary" style names)
    //   - first-word's initial + exact last word, concatenated with no
    //     separator ("MScissors" for "Metal cutting scissors" - EFT's
    //     abbreviation for names too long to show in full drops every word
    //     but the first initial and the last word)
    // Deliberately narrower than "any word matches" - allowing a match on
    // any middle word would let a generic word like "magazine" match
    // again through this path.
    private static bool IsWordMatch(string fullName, string normalizedQuery)
    {
        var words = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return false;
        }

        var firstWord = Normalize(words[0]);
        var lastWord = Normalize(words[^1]);

        if (normalizedQuery == firstWord || normalizedQuery == lastWord)
        {
            return true;
        }

        if (firstWord.Length > 0 && normalizedQuery == firstWord[0] + lastWord)
        {
            return true;
        }

        return false;
    }
}
