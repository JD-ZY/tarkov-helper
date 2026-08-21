using TarkovHelper.Core;
using TarkovHelper.Core.Models;

namespace TarkovHelper.Core.Tests;

public class ItemLookupTests
{
    private static QuestTask MakeTaskWithObjectiveDescription(string id, params string[] descriptions)
    {
        return new QuestTask
        {
            Id = id,
            Name = id,
            Trader = new Trader { Name = "Prapor" },
            Objectives = descriptions.Select(d => new TaskObjective { Description = d }).ToList(),
        };
    }

    [Fact]
    public void MatchesTaskWhoseObjectiveDescriptionContainsItemName()
    {
        var task = MakeTaskWithObjectiveDescription("t1", "Find a bronze pocket watch");
        var results = ItemLookup.FindTasksReferencingItem(new[] { task }, "pocket watch");

        Assert.Single(results);
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        var task = MakeTaskWithObjectiveDescription("t1", "Find a Bronze Pocket Watch");
        var results = ItemLookup.FindTasksReferencingItem(new[] { task }, "BRONZE POCKET WATCH");

        Assert.Single(results);
    }

    [Fact]
    public void NonMatchingQuery_ReturnsEmpty()
    {
        var task = MakeTaskWithObjectiveDescription("t1", "Eliminate 5 Scavs with a pistol");
        var results = ItemLookup.FindTasksReferencingItem(new[] { task }, "pocket watch");

        Assert.Empty(results);
    }

    [Fact]
    public void EmptyQuery_ReturnsEmpty()
    {
        var task = MakeTaskWithObjectiveDescription("t1", "Find a bronze pocket watch");
        var results = ItemLookup.FindTasksReferencingItem(new[] { task }, "");

        Assert.Empty(results);
    }

    [Fact]
    public void MultipleTasksCanReferenceSameItem()
    {
        var t1 = MakeTaskWithObjectiveDescription("t1", "Find a bronze pocket watch");
        var t2 = MakeTaskWithObjectiveDescription("t2", "Hand over 3 bronze pocket watches");
        var results = ItemLookup.FindTasksReferencingItem(new[] { t1, t2 }, "pocket watch");

        Assert.Equal(2, results.Count);
    }

    private static QuestTask MakeTaskWithItemObjective(
        string id, bool isActive, bool isComplete, string itemId, string itemName, string description, int count = 1, bool foundInRaid = false)
    {
        return new QuestTask
        {
            Id = id,
            Name = id,
            IsActive = isActive,
            IsComplete = isComplete,
            Trader = new Trader { Name = "Prapor" },
            Objectives = new List<TaskObjective>
            {
                new()
                {
                    Description = description,
                    Count = count,
                    FoundInRaid = foundInRaid,
                    Items = new List<Item> { new() { Id = itemId, Name = itemName } },
                },
            },
        };
    }

    [Fact]
    public void FindQuestNeedsForItemId_ActiveTask_MatchesAndMarkedAvailableNow()
    {
        var task = MakeTaskWithItemObjective("t1", isActive: true, isComplete: false, "item1", "Bronze pocket watch", "Find a bronze pocket watch", count: 1, foundInRaid: true);
        var tasksById = QuestAvailability.IndexById(new[] { task });

        var needs = ItemLookup.FindQuestNeedsForItemId(new[] { task }, "item1", tasksById);

        var need = Assert.Single(needs);
        Assert.Equal("t1", need.SourceName);
        Assert.Equal(1, need.Count);
        Assert.True(need.FoundInRaid);
        Assert.True(need.IsAvailableNow);
    }

    [Fact]
    public void FindQuestNeedsForItemId_NotYetStartedButAvailable_MatchesAndMarkedAvailableNow()
    {
        // Real change: previously excluded entirely (only IsActive tasks
        // were checked) - now a "keep or sell" decision should surface a
        // quest you could accept right now even if you haven't yet,
        // distinguished from one still locked behind a prerequisite.
        var task = MakeTaskWithItemObjective("t1", isActive: false, isComplete: false, "item1", "Bronze pocket watch", "Find a bronze pocket watch");
        var tasksById = QuestAvailability.IndexById(new[] { task });

        var needs = ItemLookup.FindQuestNeedsForItemId(new[] { task }, "item1", tasksById);

        var need = Assert.Single(needs);
        Assert.True(need.IsAvailableNow);
    }

    [Fact]
    public void FindQuestNeedsForItemId_LockedBehindPrerequisite_MatchesButMarkedNotAvailableNow()
    {
        var prereq = new QuestTask { Id = "prereq", Name = "prereq", IsComplete = false, Trader = new Trader() };
        var task = MakeTaskWithItemObjective("t1", isActive: false, isComplete: false, "item1", "Bronze pocket watch", "Find a bronze pocket watch");
        task.TaskRequirements.Add(new TaskStatusRequirement { Task = new TaskRef { Id = "prereq" }, Status = new List<string> { "complete" } });
        var tasksById = QuestAvailability.IndexById(new[] { prereq, task });

        var needs = ItemLookup.FindQuestNeedsForItemId(new[] { task }, "item1", tasksById);

        var need = Assert.Single(needs);
        Assert.False(need.IsAvailableNow);
    }

    [Fact]
    public void FindQuestNeedsForItemId_CompletedTask_Excluded()
    {
        var task = MakeTaskWithItemObjective("t1", isActive: true, isComplete: true, "item1", "Bronze pocket watch", "Find a bronze pocket watch");
        var tasksById = QuestAvailability.IndexById(new[] { task });

        var needs = ItemLookup.FindQuestNeedsForItemId(new[] { task }, "item1", tasksById);

        Assert.Empty(needs);
    }

    [Fact]
    public void FindQuestNeedsForItemId_DifferentItemId_NoMatch()
    {
        var task = MakeTaskWithItemObjective("t1", isActive: true, isComplete: false, "item1", "Bronze pocket watch", "Find a bronze pocket watch");
        var tasksById = QuestAvailability.IndexById(new[] { task });

        var needs = ItemLookup.FindQuestNeedsForItemId(new[] { task }, "item2", tasksById);

        Assert.Empty(needs);
    }

    [Fact]
    public void FindHideoutNeedsForItemId_MatchesNextUnbuiltLevel_MarkedAvailableNow()
    {
        var station = new HideoutStation
        {
            Id = "s1",
            Name = "Workbench",
            CurrentLevel = 1,
            Levels = new List<HideoutLevel>
            {
                new() { Level = 1, ItemRequirements = new List<HideoutItemRequirement>() },
                new()
                {
                    Level = 2,
                    ItemRequirements = new List<HideoutItemRequirement>
                    {
                        new() { Item = new Item { Id = "item1", Name = "Duct tape" }, Count = 5, FoundInRaid = true },
                    },
                },
            },
        };

        var needs = ItemLookup.FindHideoutNeedsForItemId(new[] { station }, "item1");

        var need = Assert.Single(needs);
        Assert.Equal("Workbench", need.SourceName);
        Assert.Equal(5, need.Count);
        Assert.True(need.FoundInRaid);
        Assert.True(need.IsAvailableNow);
    }

    [Fact]
    public void FindHideoutNeedsForItemId_FutureLevelBeyondNext_MatchesButMarkedNotAvailableNow()
    {
        // Real change: previously excluded entirely (only the immediate
        // next level was checked) - now a level 2 steps away still shows
        // up, just flagged as not buildable yet.
        var station = new HideoutStation
        {
            Id = "s1",
            Name = "Workbench",
            CurrentLevel = 1,
            Levels = new List<HideoutLevel>
            {
                new() { Level = 1, ItemRequirements = new List<HideoutItemRequirement>() },
                new() { Level = 2, ItemRequirements = new List<HideoutItemRequirement>() },
                new()
                {
                    Level = 3,
                    ItemRequirements = new List<HideoutItemRequirement>
                    {
                        new() { Item = new Item { Id = "item1", Name = "Duct tape" }, Count = 5 },
                    },
                },
            },
        };

        var needs = ItemLookup.FindHideoutNeedsForItemId(new[] { station }, "item1");

        var need = Assert.Single(needs);
        Assert.False(need.IsAvailableNow);
    }

    [Fact]
    public void FindHideoutNeedsForItemId_AlreadyBuiltLevel_Excluded()
    {
        var station = new HideoutStation
        {
            Id = "s1",
            Name = "Workbench",
            CurrentLevel = 2,
            Levels = new List<HideoutLevel>
            {
                new()
                {
                    Level = 2,
                    ItemRequirements = new List<HideoutItemRequirement>
                    {
                        new() { Item = new Item { Id = "item1", Name = "Duct tape" }, Count = 5 },
                    },
                },
            },
        };

        var needs = ItemLookup.FindHideoutNeedsForItemId(new[] { station }, "item1");

        Assert.Empty(needs);
    }

    // Real bug reported by a user: hovering "Car battery" didn't show the
    // "Car Repair" quest, which needs it via a findItem/giveItem objective
    // with a real structured Items entry. Root cause was two-fold - (1)
    // the UI was wired to FindTasksReferencingItem (text search against
    // objective descriptions) instead of this ID-based lookup, and (2)
    // even that text search would have failed anyway since Car Repair's
    // objective text is "Find Car batteries in raid" (plural) which
    // doesn't contain "Car battery" (singular) as a substring. Car Repair
    // is also NOT ACTIVE and has an unmet prerequisite in the real data,
    // so this also verifies not-yet-available quests are still included.
    [Fact]
    public void FindTasksNeedingItemId_MatchesInactiveLockedQuestByExactItemId()
    {
        var task = new QuestTask
        {
            Id = "car-repair",
            Name = "Car Repair",
            IsActive = false,
            IsComplete = false,
            Trader = new Trader { Name = "Mechanic" },
            TaskRequirements = new List<TaskStatusRequirement>
            {
                new() { Task = new TaskRef { Id = "prereq" }, Status = new List<string> { "complete" } },
            },
            Objectives = new List<TaskObjective>
            {
                new()
                {
                    Description = "Find Car batteries in raid",
                    Type = "findItem",
                    Items = new List<Item> { new() { Id = "car-battery-id", Name = "Car battery" } },
                },
                new()
                {
                    Description = "Hand over the batteries",
                    Type = "giveItem",
                    Items = new List<Item> { new() { Id = "car-battery-id", Name = "Car battery" } },
                },
            },
        };

        var results = ItemLookup.FindTasksNeedingItemId(new[] { task }, "car-battery-id");

        Assert.Single(results);
        Assert.Equal("Car Repair", results[0].Name);
    }

    [Fact]
    public void FindTasksNeedingItemId_TextSearchWouldHaveMissedPluralMismatch_IdSearchDoesNot()
    {
        // Direct comparison confirming the text-search path really does
        // fail on this exact real-world case, motivating why the ID-based
        // lookup is the one wired into the UI.
        var task = new QuestTask
        {
            Id = "car-repair",
            Name = "Car Repair",
            Trader = new Trader { Name = "Mechanic" },
            Objectives = new List<TaskObjective>
            {
                new()
                {
                    Description = "Find Car batteries in raid",
                    Items = new List<Item> { new() { Id = "car-battery-id", Name = "Car battery" } },
                },
            },
        };

        var textSearchResults = ItemLookup.FindTasksReferencingItem(new[] { task }, "Car battery");
        var idSearchResults = ItemLookup.FindTasksNeedingItemId(new[] { task }, "car-battery-id");

        Assert.Empty(textSearchResults);
        Assert.Single(idSearchResults);
    }

    [Fact]
    public void FindTasksNeedingItemId_CompletedTask_Excluded()
    {
        var task = new QuestTask
        {
            Id = "t1",
            Name = "t1",
            IsComplete = true,
            Trader = new Trader { Name = "Prapor" },
            Objectives = new List<TaskObjective>
            {
                new() { Description = "x", Items = new List<Item> { new() { Id = "item1", Name = "Item" } } },
            },
        };

        var results = ItemLookup.FindTasksNeedingItemId(new[] { task }, "item1");

        Assert.Empty(results);
    }

    [Fact]
    public void FindTasksNeedingItemId_DifferentItemId_NoMatch()
    {
        var task = new QuestTask
        {
            Id = "t1",
            Name = "t1",
            Trader = new Trader { Name = "Prapor" },
            Objectives = new List<TaskObjective>
            {
                new() { Description = "x", Items = new List<Item> { new() { Id = "item1", Name = "Item" } } },
            },
        };

        var results = ItemLookup.FindTasksNeedingItemId(new[] { task }, "item2");

        Assert.Empty(results);
    }

    // Real bug reported by a user: "Building Foundations" and "Key
    // Partner" appeared for nearly every item hover-lookup. Root cause:
    // both have sellItem objectives ("Sell any items to Prapor") whose
    // Items list legitimately enumerates 3000+ eligible items (real data
    // confirmed: 3315, 3315, 3493, and 3313 entries across their
    // objectives) - that's "any ONE of these thousands satisfies this
    // step," not "you specifically need this exact item," but the ID
    // match treated a hit anywhere in that list the same as a genuine
    // findItem/giveItem requirement.
    [Fact]
    public void FindTasksNeedingItemId_HugeSellItemPool_ExcludedEvenWhenItemPresent()
    {
        var hugeItemList = Enumerable.Range(0, 3315)
            .Select(i => new Item { Id = $"item{i}", Name = $"Item {i}" })
            .ToList();
        var task = new QuestTask
        {
            Id = "building-foundations",
            Name = "Building Foundations",
            Trader = new Trader { Name = "Prapor" },
            Objectives = new List<TaskObjective>
            {
                new() { Description = "Sell any items to Prapor", Type = "sellItem", Items = hugeItemList },
            },
        };

        // "item500" really is in the huge pool, but should still not match.
        var results = ItemLookup.FindTasksNeedingItemId(new[] { task }, "item500");

        Assert.Empty(results);
    }

    [Fact]
    public void FindQuestNeedsForItemId_HugeSellItemPool_ExcludedEvenWhenItemPresent()
    {
        var hugeItemList = Enumerable.Range(0, 3315)
            .Select(i => new Item { Id = $"item{i}", Name = $"Item {i}" })
            .ToList();
        var task = new QuestTask
        {
            Id = "building-foundations",
            Name = "Building Foundations",
            IsActive = true,
            Trader = new Trader { Name = "Prapor" },
            Objectives = new List<TaskObjective>
            {
                new() { Description = "Sell any items to Prapor", Type = "sellItem", Items = hugeItemList },
            },
        };
        var tasksById = QuestAvailability.IndexById(new[] { task });

        var needs = ItemLookup.FindQuestNeedsForItemId(new[] { task }, "item500", tasksById);

        Assert.Empty(needs);
    }

    [Fact]
    public void FindTasksNeedingItemId_SmallItemPoolStillMatches_NotJustHugeOnesExcluded()
    {
        // Confirms the fix is scoped to implausibly large pools, not item
        // matching in general - a genuine small "any of these few
        // variants" objective (like Chumming's golden chains) must still
        // match normally.
        var task = new QuestTask
        {
            Id = "t1",
            Name = "t1",
            Trader = new Trader { Name = "Prapor" },
            Objectives = new List<TaskObjective>
            {
                new()
                {
                    Description = "Find one of these",
                    Type = "findItem",
                    Items = new List<Item>
                    {
                        new() { Id = "item1", Name = "Item 1" },
                        new() { Id = "item2", Name = "Item 2" },
                        new() { Id = "item3", Name = "Item 3" },
                    },
                },
            },
        };

        var results = ItemLookup.FindTasksNeedingItemId(new[] { task }, "item2");

        Assert.Single(results);
    }

    [Fact]
    public void ResolveItemNameFuzzy_ExactMatch_ReturnsItem()
    {
        var names = new Dictionary<string, string> { ["item1"] = "Bronze pocket watch" };

        var result = ItemLookup.ResolveItemNameFuzzy(names, "Bronze pocket watch");

        Assert.NotNull(result);
        Assert.Equal("item1", result!.Value.ItemId);
    }

    [Fact]
    public void ResolveItemNameFuzzy_OcrNoiseWithExtraSpacesAndPunctuation_StillMatches()
    {
        // Simulates realistic OCR noise: extra/missing spaces, stray
        // punctuation, and case differences - the kind of error OCR
        // produces on EFT's small stylized tooltip font, per real-world
        // reports from similar tools (TarkovPriceViewer, Tarkov Price
        // Overlay), rather than scrambled/substituted letters.
        var names = new Dictionary<string, string> { ["item1"] = "Bronze pocket watch" };

        var result = ItemLookup.ResolveItemNameFuzzy(names, "BRONZE POCKETWATCH.");

        Assert.NotNull(result);
        Assert.Equal("item1", result!.Value.ItemId);
    }

    [Fact]
    public void ResolveItemNameFuzzy_NoReasonableMatch_ReturnsNull()
    {
        var names = new Dictionary<string, string> { ["item1"] = "Bronze pocket watch" };

        var result = ItemLookup.ResolveItemNameFuzzy(names, "xyz garbled ocr output 123");

        Assert.Null(result);
    }

    // Real bug reported by a user: item lookups for magazines/ammo/gun
    // parts sometimes returned just "Magazine" - a generic category label
    // rather than a real item name. Root cause: OCR read a short generic
    // word from the UI, and the old substring-match rule accepted it
    // because "magazine" is literally a substring of hundreds of real item
    // names ("AK-74 5.45x39 6L23 30-round magazine" etc.) - the old score
    // (min(name,query) length) easily cleared the flat floor regardless of
    // how small a fraction of the real name that word actually covered.
    [Fact]
    public void ResolveItemNameFuzzy_GenericCategoryWord_DoesNotMatchLongRealItemName()
    {
        // Realistic scale matters here: "magazine" is the exact last word
        // of hundreds of real item names, which is also the shape of a
        // genuine EFT abbreviation (e.g. "Hose" for "Corrugated hose") -
        // only catalog-wide scale (many candidates sharing that same last
        // word) distinguishes "generic category word" from "specific
        // abbreviation". A 2-item fixture doesn't exercise that
        // distinction, so this uses enough magazine names to look like
        // the real catalog.
        var names = new Dictionary<string, string>();
        for (var i = 0; i < 20; i++)
        {
            names[$"item{i}"] = $"Magazine variant {i} 30-round magazine";
        }

        var result = ItemLookup.ResolveItemNameFuzzy(names, "Magazine");

        Assert.Null(result);
    }

    // Real bug: EFT's own stash-grid caption labels are genuinely
    // abbreviated short names, not the item's full catalog name and not
    // an OCR error - confirmed directly against a real capture where the
    // game itself displayed "Hose" for "Corrugated hose" and "MScissors"
    // for "Metal cutting scissors" (first-word-initial + exact last word).
    // The original >=70%-length-ratio requirement blocked these
    // legitimate short captions from ever matching their real, much
    // longer full name.
    [Theory]
    [InlineData("Hose", "item1")]
    [InlineData("MScissors", "item2")]
    public void ResolveItemNameFuzzy_RealEftAbbreviation_MatchesFullItemName(string caption, string expectedId)
    {
        var names = new Dictionary<string, string>
        {
            ["item1"] = "Corrugated hose",
            ["item2"] = "Metal cutting scissors",
            ["item3"] = "Bronze pocket watch",
        };

        var result = ItemLookup.ResolveItemNameFuzzy(names, caption);

        Assert.NotNull(result);
        Assert.Equal(expectedId, result!.Value.ItemId);
    }

    [Fact]
    public void ResolveItemNameFuzzy_ShortRealItemName_StillMatchesShortOcrText()
    {
        // Confirms the fix doesn't break genuinely short real item names -
        // "Magazine" should still match an item whose real full name is
        // itself short, just not one where it's a small fragment of a
        // much longer name.
        var names = new Dictionary<string, string> { ["item1"] = "Magazine" };

        var result = ItemLookup.ResolveItemNameFuzzy(names, "Magazine");

        Assert.NotNull(result);
        Assert.Equal("item1", result!.Value.ItemId);
    }

    [Fact]
    public void ResolveItemNameFuzzy_EmptyText_ReturnsNull()
    {
        var names = new Dictionary<string, string> { ["item1"] = "Bronze pocket watch" };

        var result = ItemLookup.ResolveItemNameFuzzy(names, "   ");

        Assert.Null(result);
    }

    [Fact]
    public void ResolveItemNameFuzzy_PrefersBetterOverlapAmongMultipleCandidates()
    {
        var names = new Dictionary<string, string>
        {
            ["item1"] = "Bronze pocket watch",
            ["item2"] = "Watch",
        };

        var result = ItemLookup.ResolveItemNameFuzzy(names, "Bronze pocket watch");

        Assert.NotNull(result);
        Assert.Equal("item1", result!.Value.ItemId);
    }

    // Real bug reproduced by a user: a wide screenshot region around the
    // cursor (needed because the tooltip's position relative to the cursor
    // varies by UI context) picks up multiple lines of OCR text - stack
    // counts, durability numbers ("220/220"), background UI text - only
    // one of which is the real item name. ResolveBestItemMatch must find
    // the real name among the noise, not just take the first/nearest line.
    [Fact]
    public void ResolveBestItemMatch_RealNameAmongDurabilityAndStackNoise_FindsRealName()
    {
        var names = new Dictionary<string, string> { ["item1"] = "Car first aid kit" };
        var candidateLines = new[] { "206/206", "220/220", "Car first aid kit", "6B34" };

        var result = ItemLookup.ResolveBestItemMatch(names, candidateLines);

        Assert.NotNull(result);
        Assert.Equal("item1", result!.Value.ItemId);
    }

    [Fact]
    public void ResolveBestItemMatch_NoLineMatchesAnything_ReturnsNull()
    {
        var names = new Dictionary<string, string> { ["item1"] = "Car first aid kit" };
        var candidateLines = new[] { "206/206", "5/55 | EL) a |", "2204224) vy | 30738 O 1" };

        var result = ItemLookup.ResolveBestItemMatch(names, candidateLines);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveBestItemMatch_EmptyCandidateList_ReturnsNull()
    {
        var names = new Dictionary<string, string> { ["item1"] = "Car first aid kit" };

        var result = ItemLookup.ResolveBestItemMatch(names, Array.Empty<string>());

        Assert.Null(result);
    }

    // Real bug reproduced by a user: hovering "Bulb" (a real, short item
    // name) picked "Vaseline balm" instead, because a much longer,
    // unrelated item name elsewhere in the wide capture region out-scored
    // the real hovered item on pure string-length overlap, with no
    // positional information to prefer the line actually under the
    // cursor. The distance-weighted overload must prefer the close-but-
    // shorter match over the far-but-longer one.
    [Fact]
    public void ResolveBestItemMatch_WithDistance_PrefersCloseShortMatchOverFarLongMatch()
    {
        var names = new Dictionary<string, string>
        {
            ["item1"] = "Bulb",
            ["item2"] = "Vaseline balm",
        };
        var candidates = new (string, float)[]
        {
            ("Bulb", 0.05f),           // right at the cursor
            ("Vaseline balm", 0.9f),   // far from the cursor
        };

        var result = ItemLookup.ResolveBestItemMatch(names, candidates);

        Assert.NotNull(result);
        Assert.Equal("item1", result!.Value.ItemId);
    }

    [Fact]
    public void ResolveBestItemMatch_WithDistance_StrongFarMatchCanStillWinOverWeakCloseMatch()
    {
        // Proximity should tip close calls, not override an actually
        // strong match - if the line at the cursor barely matches
        // anything while a full exact match sits nearby (still not at the
        // extreme far edge), the real match should still be found.
        var names = new Dictionary<string, string>
        {
            ["item1"] = "Golden neck chain",
        };
        var candidates = new (string, float)[]
        {
            ("xyz", 0.0f),
            ("Golden neck chain", 0.3f),
        };

        var result = ItemLookup.ResolveBestItemMatch(names, candidates);

        Assert.NotNull(result);
        Assert.Equal("item1", result!.Value.ItemId);
    }

    [Fact]
    public void ResolveBestItemMatch_StringOnlyOverload_TreatsAllCandidatesAsEquallyClose()
    {
        // Backward-compat overload (no positional data available) - should
        // behave like distance-weight 0 for everything, i.e. pure text
        // scoring, same as before this fix.
        var names = new Dictionary<string, string> { ["item1"] = "Bronze pocket watch" };

        var result = ItemLookup.ResolveBestItemMatch(names, new[] { "Bronze pocket watch" });

        Assert.NotNull(result);
        Assert.Equal("item1", result!.Value.ItemId);
    }
}
