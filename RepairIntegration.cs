#nullable enable

using HarmonyLib;
using System;
using System.Collections.Generic;

namespace SilverpineMods.CustomItemLoader;

/// <summary>
/// Extends Silverpine's name-based ore filter without replacing it. The
/// original repair calculation continues to use each selected item's final
/// gold value and therefore remains compatible with quality/value modifiers.
/// </summary>
[HarmonyPatch(
    typeof(RepairCraftingUIData),
    nameof(RepairCraftingUIData.GetItemSelectionFieldInfos))]
internal static class CustomRepairMaterialSelectionPatch
{
    private static void Postfix(ref List<ItemSelectionFieldInfo> __result)
    {
        if (__result == null ||
            __result.Count < 2 ||
            Plugin.RepairMaterialSpriteKeys.Count == 0)
            return;

        // Field zero is the item being repaired. Preserve it exactly and
        // extend only the four native ore/material fields that follow it.
        for (int index = 1; index < __result.Count; index++)
        {
            ItemSelectionFieldInfo field = __result[index];
            Predicate<Item> originalPredicate = field.predicate;
            field.predicate = item =>
                item != null &&
                (originalPredicate(item) || IsRegisteredRepairMaterial(item));
            field.label = "Repair Material";
        }
    }

    private static bool IsRegisteredRepairMaterial(Item item) =>
        !string.IsNullOrWhiteSpace(item.spriteName) &&
        Plugin.RepairMaterialSpriteKeys.Contains(item.spriteName);
}
