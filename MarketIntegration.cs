#nullable enable

using HarmonyLib;
using System;
using System.Collections.Generic;

namespace SilverpineMods.CustomItemLoader;

/// <summary>
/// Reconciles custom market choices after Silverpine creates or restores the
/// save-specific market list. Running after Market.Start also makes changed
/// pack settings apply to existing saves.
/// </summary>
[HarmonyPatch(typeof(Market), "Start")]
internal static class CustomItemMarketStartPatch
{
    [HarmonyPostfix]
    private static void Postfix(Market __instance)
    {
        if (__instance.marketItems == null)
            return;

        IReadOnlyList<CustomItemInfo> customItems =
            CustomItemApi.GetRegisteredItems();
        int included = 0;
        int excluded = 0;
        foreach (CustomItemInfo customItem in customItems)
        {
            bool shouldInclude = customItem.MarketBehavior switch
            {
                CustomItemMarketBehavior.Include => true,
                CustomItemMarketBehavior.Exclude => false,
                _ => IsAutomaticallyIncluded(customItem.Template)
            };

            if (!shouldInclude)
            {
                int removed = __instance.marketItems.RemoveAll(entry =>
                    IsEntryFor(entry, customItem.DisplayName));
                excluded += removed;
                continue;
            }

            if (__instance.marketItems.Exists(entry =>
                    IsEntryFor(entry, customItem.DisplayName)))
                continue;

            Item item = customItem.Template;
            __instance.marketItems.Add(new MarketItem(
                item.name,
                item.value * 0.25f,
                item.value * 2f,
                item.value * 0.5f));
            included++;
        }

        if (included > 0 || excluded > 0)
            Plugin.Log.LogInfo(
                $"Reconciled custom market entries: added {included}, " +
                $"removed {excluded}.");
    }

    private static bool IsAutomaticallyIncluded(Item item) =>
        item.spriteName.ContainsAnyWordIgnoreCase("herb", "ore") ||
        item.name == "Turnip";

    private static bool IsEntryFor(MarketItem? entry, string itemName) =>
        entry != null &&
        string.Equals(
            entry.itemName,
            itemName,
            StringComparison.OrdinalIgnoreCase);
}
