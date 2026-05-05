using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SurtlingCoreFuel
{
    [BepInPlugin("com.MoistGravy.SurtlingCoreFuel", "Surtling Core Fuel", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal const string SurtlingCoreItemName = "SurtlingCore";
        internal const string RpcAddCoreFuel = "RPC_SCF_AddCoreFuel";

        /// <summary>ZDO float: how many of the current fuel units are core-sourced.</summary>
        private static readonly int ZdoCoreFuelKey = StableHash("SCF_CoreFuel_v2");

        public static Plugin Instance { get; private set; }
        public static ConfigEntry<int> SurtlingCoreFuelMultiplier { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            SurtlingCoreFuelMultiplier = Config.Bind(
                "General",
                "SurtlingCoreFuelMultiplier",
                5,
                "How many coal-equivalent fuel units one Surtling Core provides. " +
                "A core occupies 1 fuel slot but burns for this many times longer than coal.");

            _harmony = new Harmony("com.MoistGravy.SurtlingCoreFuel");
            _harmony.PatchAll();
            Logger.LogInfo("Surtling Core Fuel loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        // ────────────────── Helpers ──────────────────

        internal static bool IsSmelterTarget(Smelter smelter)
        {
            if (smelter == null || smelter.gameObject == null)
            {
                return false;
            }

            var name = smelter.gameObject.name.ToLowerInvariant();
            return name.Contains("smelter") || name.Contains("blastfurnace");
        }

        internal static bool IsSurtlingCoreItem(ItemDrop.ItemData item)
        {
            if (item == null)
            {
                return false;
            }

            return IsSurtlingCoreName(item.m_dropPrefab?.name) || IsSurtlingCoreName(item.m_shared?.m_name);
        }

        internal static bool IsSurtlingCoreName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (name.StartsWith("$item_", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(6);
            }

            return string.Equals(name, SurtlingCoreItemName, StringComparison.OrdinalIgnoreCase);
        }

        private static int StableHash(string text)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (var c in text)
                {
                    h = (h ^ c) * 16777619;
                }

                return (int)h;
            }
        }

        private static ZNetView GetNView(Smelter smelter)
        {
            return AccessTools.Field(typeof(Smelter), "m_nview").GetValue(smelter) as ZNetView;
        }

        internal static ItemDrop.ItemData FindSurtlingCoreInInventory(Inventory inventory)
        {
            if (inventory == null)
            {
                return null;
            }

            foreach (var stack in inventory.GetAllItems())
            {
                if (IsSurtlingCoreItem(stack))
                {
                    return stack;
                }
            }

            return null;
        }

        // ────────────────── RPC Handler ──────────────────

        internal static void RpcAddCoreFuelHandler(ZNetView nview, Smelter smelter, int unused)
        {
            if (nview == null || smelter == null || !nview.IsOwner())
            {
                return;
            }

            var zdo = nview.GetZDO();
            if (zdo == null)
            {
                return;
            }

            var current = zdo.GetFloat(ZDOVars.s_fuel, 0f);
            var space = smelter.m_maxFuel - Mathf.CeilToInt(current);
            if (space <= 0)
            {
                return;
            }

            // Add exactly 1 fuel unit (same footprint as 1 coal)
            zdo.Set(ZDOVars.s_fuel, current + 1f);

            // Track it as core fuel
            var coreFuel = zdo.GetFloat(ZdoCoreFuelKey, 0f);
            zdo.Set(ZdoCoreFuelKey, coreFuel + 1f);

            if (smelter.m_fuelAddedEffects != null)
            {
                smelter.m_fuelAddedEffects.Create(
                    smelter.transform.position,
                    smelter.transform.rotation,
                    smelter.transform, 1f, -1);
            }
        }

        // ────────────────── Smelter.Awake — register RPC ──────────────────

        [HarmonyPatch(typeof(Smelter), "Awake")]
        internal static class SmelterAwakePatch
        {
            private static void Postfix(Smelter __instance)
            {
                if (!IsSmelterTarget(__instance))
                {
                    return;
                }

                var nview = GetNView(__instance);
                if (nview == null || nview.GetZDO() == null)
                {
                    return;
                }

                nview.Register<int>(RpcAddCoreFuel, (Action<long, int>)((sender, fuel) =>
                {
                    RpcAddCoreFuelHandler(nview, __instance, fuel);
                }));
            }
        }

        // ────────────────── OnAddFuel — intercept surtling core insertion ──────────────────

        [HarmonyPatch(typeof(Smelter), "OnAddFuel", typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData))]
        internal static class SmelterOnAddFuelPatch
        {
            internal static bool Prefix(Smelter __instance, Switch sw, Humanoid user, ItemDrop.ItemData item, ref bool __result)
            {
                if (!IsSmelterTarget(__instance) || user == null || user.GetInventory() == null)
                {
                    return true;
                }

                var nview = GetNView(__instance);
                if (nview == null || !nview.IsValid())
                {
                    return true;
                }

                var zdo = nview.GetZDO();
                if (zdo == null)
                {
                    return true;
                }

                var currentFuel = zdo.GetFloat(ZDOVars.s_fuel, 0f);
                ItemDrop.ItemData coreItem = null;

                if (item != null && IsSurtlingCoreItem(item))
                {
                    coreItem = item;
                }
                else if (item == null)
                {
                    // Auto-select: prefer vanilla coal if available, fall back to cores
                    if (__instance.m_fuelItem != null)
                    {
                        var fuelName = __instance.m_fuelItem.m_itemData.m_shared.m_name;
                        if (user.GetInventory().HaveItem(fuelName, true))
                        {
                            return true; // let vanilla handle coal
                        }
                    }

                    coreItem = FindSurtlingCoreInInventory(user.GetInventory());
                }

                if (coreItem == null)
                {
                    return true;
                }

                // Check space for 1 slot
                if (currentFuel > __instance.m_maxFuel - 1f)
                {
                    __result = false;
                    return false;
                }

                user.GetInventory().RemoveItem(coreItem, 1);
                nview.InvokeRPC(RpcAddCoreFuel, new object[] { 1 });

                __result = true;
                return false;
            }
        }

        // ────────────────── IsItemAllowed overloads ──────────────────

        [HarmonyPatch(typeof(Smelter), "IsItemAllowed", typeof(ItemDrop.ItemData))]
        internal static class SmelterIsItemAllowedItemPatch
        {
            private static void Postfix(Smelter __instance, ItemDrop.ItemData item, ref bool __result)
            {
                if (!IsSmelterTarget(__instance) || item == null)
                {
                    return;
                }

                if (IsSurtlingCoreItem(item))
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(Smelter), "IsItemAllowed", typeof(string))]
        internal static class SmelterIsItemAllowedStringPatch
        {
            private static void Postfix(Smelter __instance, string itemName, ref bool __result)
            {
                if (!IsSmelterTarget(__instance) || string.IsNullOrEmpty(itemName))
                {
                    return;
                }

                if (IsSurtlingCoreName(itemName))
                {
                    __result = true;
                }
            }
        }

        // ────────────────── Hover text ──────────────────

        [HarmonyPatch(typeof(Smelter), "OnHoverAddFuel")]
        internal static class SmelterOnHoverAddFuelPatch
        {
            private static void Postfix(Smelter __instance, ref string __result)
            {
                if (!IsSmelterTarget(__instance) || __result == null || __instance.m_fuelItem == null)
                {
                    return;
                }

                var coalName = __instance.m_fuelItem.m_itemData.m_shared.m_name;
                if (__result.Contains(coalName))
                {
                    __result = __result.Replace(coalName, coalName + " / Surtling Core");
                }
            }
        }

        // ────────────────── Core mechanic: inflate / deflate around UpdateSmelter ──────────────────
        //
        // Strategy:
        //   Prefix  — inflate core fuel by the multiplier so the game "sees" more fuel
        //   Postfix — deflate back, attributing burn proportionally
        //
        // FIFO model: coal burns first, then core fuel.
        //   inflated = coalPortion + coreFuel * multiplier
        //   After the game burns some of that inflated total, we figure out how much
        //   coal vs core was consumed and write the deflated values back.

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        internal static class SmelterUpdateSmelterPatch
        {
            // Prefix → Postfix state (single-threaded, safe)
            private static bool s_active;
            private static float s_origFuel;
            private static float s_origCoreFuel;
            private static float s_inflatedTotal;
            private static float s_coalPortion;
            private static float s_inflatedCorePortion;
            private static Smelter s_smelter;

            private static void Prefix(Smelter __instance)
            {
                s_active = false;

                if (!IsSmelterTarget(__instance))
                {
                    return;
                }

                var nview = GetNView(__instance);
                if (nview == null || !nview.IsValid() || !nview.IsOwner())
                {
                    return;
                }

                var zdo = nview.GetZDO();
                if (zdo == null)
                {
                    return;
                }

                var fuel = zdo.GetFloat(ZDOVars.s_fuel, 0f);
                var coreFuel = zdo.GetFloat(ZdoCoreFuelKey, 0f);

                // Clamp core fuel to never exceed total fuel
                if (coreFuel > fuel)
                {
                    coreFuel = fuel;
                }

                // Nothing to inflate
                if (coreFuel <= 0.0001f)
                {
                    return;
                }

                var m = Mathf.Max(1, SurtlingCoreFuelMultiplier?.Value ?? 1);
                if (m <= 1)
                {
                    return;
                }

                // Save original values
                s_origFuel = fuel;
                s_origCoreFuel = coreFuel;
                s_coalPortion = fuel - coreFuel;
                s_inflatedCorePortion = coreFuel * m;
                s_inflatedTotal = s_coalPortion + s_inflatedCorePortion;
                s_smelter = __instance;

                // Write inflated fuel so the game burns through it slowly
                zdo.Set(ZDOVars.s_fuel, s_inflatedTotal);
                s_active = true;
            }

            private static void Postfix(Smelter __instance)
            {
                if (!s_active || __instance != s_smelter)
                {
                    return;
                }

                s_active = false;

                var nview = GetNView(__instance);
                if (nview == null || !nview.IsValid())
                {
                    return;
                }

                var zdo = nview.GetZDO();
                if (zdo == null)
                {
                    return;
                }

                var m = Mathf.Max(1, SurtlingCoreFuelMultiplier?.Value ?? 1);
                var newInflated = zdo.GetFloat(ZDOVars.s_fuel, 0f);

                // How much total inflated fuel was burned this frame
                var burned = s_inflatedTotal - newInflated;

                if (burned <= 0.0001f)
                {
                    // Nothing was consumed — restore original values exactly
                    zdo.Set(ZDOVars.s_fuel, s_origFuel);
                    return;
                }

                // FIFO: coal burns first, then core fuel
                float newCoal;
                float newCoreDeflated;

                if (burned <= s_coalPortion)
                {
                    // Only coal was burned
                    newCoal = s_coalPortion - burned;
                    newCoreDeflated = s_origCoreFuel;
                }
                else
                {
                    // All coal is gone; remainder burned from inflated core pool
                    newCoal = 0f;
                    var coreBurnedInflated = burned - s_coalPortion;
                    var remainingInflatedCore = s_inflatedCorePortion - coreBurnedInflated;
                    newCoreDeflated = Mathf.Max(0f, remainingInflatedCore / m);
                }

                var newFuel = Mathf.Max(0f, newCoal + newCoreDeflated);

                zdo.Set(ZDOVars.s_fuel, newFuel);
                zdo.Set(ZdoCoreFuelKey, Mathf.Max(0f, newCoreDeflated));
            }
        }
    }
}
