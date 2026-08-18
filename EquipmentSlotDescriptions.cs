#nullable enable

using HarmonyLib;
using System;

namespace SilverpineMods.CustomItemLoader;

/// <summary>
/// Silverpine's clothing suffix reads the cloned clothing component's private
/// enum field directly. Custom items resolve their real slot through
/// Item.GetEquipmentSlotType instead, so normalize the final inventory text
/// after all native component suffixes have been assembled.
/// </summary>
[HarmonyPatch(typeof(Item), nameof(Item.GetFinalDescription))]
internal static class CustomItemEquipmentSlotDescriptionPatch
{
    private const string SlotPrefix = "Slot: <color=\"yellow\">";
    private const string ColorSuffix = "</color>";

    private static void Postfix(Item __instance, ref string __result)
    {
        if (__instance == null ||
            string.IsNullOrWhiteSpace(__instance.spriteName) ||
            !Plugin.EquipmentSlotOverrides.TryGetValue(
                __instance.spriteName,
                out EquipmentSlotType slotType))
            return;

        string displayName =
            CustomEquipmentSlotRegistry.TryGetSlot(
                (int)slotType,
                out CustomEquipmentSlotInfo slot)
                ? slot.DisplayName
                : slotType.ToString();

        int slotPrefixIndex = __result.IndexOf(
            SlotPrefix,
            StringComparison.Ordinal);
        if (slotPrefixIndex >= 0)
        {
            int valueStart = slotPrefixIndex + SlotPrefix.Length;
            int valueEnd = __result.IndexOf(
                ColorSuffix,
                valueStart,
                StringComparison.Ordinal);
            if (valueEnd >= 0)
            {
                __result =
                    __result.Substring(0, valueStart) +
                    displayName +
                    __result.Substring(valueEnd);
                return;
            }
        }

        string addition = SlotPrefix + displayName + ColorSuffix;
        int categoryIndex = __result.LastIndexOf(
            "Category:",
            StringComparison.Ordinal);
        __result = categoryIndex < 0
            ? __result + "\n\n" + addition
            : __result.Substring(0, categoryIndex) + addition + "\n\n" +
              __result.Substring(categoryIndex);
    }
}
