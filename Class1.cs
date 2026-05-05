using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SurtlingCoreFuel
{
    [BepInPlugin("com.MoistGravy.SurtlingCoreFuel", "Surtling Core Fuel", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal const string SurtlingCoreItemName = "SurtlingCore";
        internal const string RpcAddCoreFuel = "RPC_SCF_AddCoreFuel";

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
                "How many coal-equivalent fuel units one Surtling Core provides (1 unit = the same fuel as 1 piece of coal for that smelter or blast furnace).");

            _harmony = new Harmony("com.MoistGravy.SurtlingCoreFuel");
            _harmony.PatchAll();
            Logger.LogInfo("Surtling Core Fuel loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

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

        internal static void RpcAddCoreFuelHandler(ZNetView nview, Smelter smelter, int fuelToAdd)
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

            var applied = Mathf.Min(fuelToAdd, space);
            zdo.Set(ZDOVars.s_fuel, current + applied);

            if (smelter.m_fuelAddedEffects != null)
            {
                smelter.m_fuelAddedEffects.Create(smelter.transform.position, smelter.transform.rotation, smelter.transform, 1f, -1);
            }
        }

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
                    if (__instance.m_fuelItem != null)
                    {
                        var fuelName = __instance.m_fuelItem.m_itemData.m_shared.m_name;
                        if (user.GetInventory().HaveItem(fuelName, true))
                        {
                            return true;
                        }
                    }

                    coreItem = FindSurtlingCoreInInventory(user.GetInventory());
                }

                if (coreItem == null)
                {
                    return true;
                }

                if (currentFuel > __instance.m_maxFuel - 1f)
                {
                    __result = false;
                    return false;
                }

                var units = Mathf.Max(1, SurtlingCoreFuelMultiplier?.Value ?? 1);
                var space = __instance.m_maxFuel - Mathf.CeilToInt(currentFuel);
                var toAdd = Mathf.Min(units, space);

                user.GetInventory().RemoveItem(coreItem, 1);
                nview.InvokeRPC(RpcAddCoreFuel, new object[] { toAdd });

                __result = true;
                return false;
            }
        }

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
    }
}
