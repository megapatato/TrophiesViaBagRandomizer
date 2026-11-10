using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TrophiesViaBagRandomizer;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("valheim.exe")]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
        
    private void Awake()
    {
        // Plugin startup logic
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} has arrived!");
    }

    [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
    class Patch
    {
        static void PostFix(CharacterDrop __instance, ref List<KeyValuePair<GameObject, int>> __result)
        {
            foreach (KeyValuePair<GameObject, int> drop in __result)
            {
                if (drop.Key != null && drop.Key.TryGetComponent<ItemDrop>(out var component) && component.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
                {
                    Logger.LogInfo("Found trophy in drop table from built-in logic! Removing it.");
                    __result.Remove(drop);
                }
            }
            foreach (CharacterDrop.Drop drop in __instance.m_drops)
            {
                if (drop.m_prefab.TryGetComponent<ItemDrop>(out var component) && component.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
                {
                    Logger.LogInfo("Drop event eligible to have dropped a trophy!");
                    
                }
            }
        }
    }
}

// Intercept the List<KeyValuePair<GameObject, int>> returned by CharacterDrop.GenerateDropList()
// Iterate and check if it contains trophies; remove them if true. See https://valheim.hexium.gg/mods/Neobotics/SlayerSkills/versions/1.2.0/decompile#sha256=ea75bb25419aa0351efbb19b81878eef1f50153323ab66b41c7a0649bce0fb2c&type=neobotics.ValheimMods.SlayerSkills&line=159
// Inject a trophy based on Bag Randomizer logic