using System;
using System.Globalization;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    private bool TryGetBeastQuestProgress(out int current, out int total)
    {
        current = 0;
        total = 0;

        var questTracker = GetQuestTracker();
        if (questTracker == null)
        {
            return false;
        }

        if (TryParseBeastQuestProgress(GetPrimaryQuestText(questTracker), out current, out total))
        {
            return true;
        }

        var questEntries = GetQuestEntriesContainer(questTracker)?.Children;
        if (questEntries == null)
        {
            return false;
        }

        foreach (var questEntry in questEntries)
        {
            if (questEntry?.IsVisible != true)
            {
                continue;
            }

            if (TryParseBeastQuestProgress(GetQuestEntryText(questEntry), out current, out total))
            {
                return true;
            }
        }

        return false;
    }

    private Element GetQuestTracker()
    {
        return GameController?.IngameState?.IngameUi?.GetChildAtIndex(4);
    }

    private static Element GetQuestEntriesContainer(Element questTracker)
    {
        return questTracker
            .GetChildAtIndex(0)
            ?.GetChildAtIndex(0)
            ?.GetChildAtIndex(0);
    }

    private static string GetPrimaryQuestText(Element questTracker)
    {
        return GetQuestEntryText(GetQuestEntriesContainer(questTracker)?.GetChildAtIndex(0));
    }

    private static string GetQuestEntryText(Element questEntry)
    {
        return questEntry
            ?.GetChildAtIndex(0)
            ?.GetChildAtIndex(1)
            ?.GetChildAtIndex(0)
            ?.GetChildAtIndex(1)
            ?.Text;
    }

    private static bool TryParseBeastQuestProgress(string questText, out int current, out int total)
    {
        current = 0;
        total = 0;

        if (string.IsNullOrWhiteSpace(questText) ||
            !questText.Contains("beast", StringComparison.OrdinalIgnoreCase) &&
            !questText.Contains("einhar", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = QuestProgressRegex.Match(questText);
        if (!match.Success)
        {
            return false;
        }

        current = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        total = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }
}
