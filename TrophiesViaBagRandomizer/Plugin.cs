using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.IO;

namespace TrophiesViaBagRandomizer;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("valheim.exe")]
public class Plugin : BaseUnityPlugin
{
    private static Harmony harmony = null;

    internal static new ManualLogSource Logger;

    public class Bag
    {
        /// <summary>
        /// The number of tries so far.
        /// </summary>
        public int tries;

        /// <summary>
        /// Size of the bag; the maximum number of tries before a trophy is
        /// awarded.
        /// </summary>
        public int size;

        /// <summary>
        /// The try that will award the trophy.
        /// </summary>
        public int winner;

        /// <summary>
        /// The thing to spawn; injected back into the GenerateDropList List<KeyValuePair<GameObject, int>> output.
        /// </summary>
        public GameObject trophy;

        public Bag(float probability, GameObject trophy_drop)
        {
            size = Mathf.FloorToInt(1/probability);
            trophy = trophy_drop;
            Reset();
        }

        private void Reset(int starting_try = 0)
        {
            if (starting_try < 0)
            {
                Logger.LogError($"Got a starting_try of {starting_try}! Resetting it to 0.");
                starting_try = 0;
            }
            winner = UnityEngine.Random.Range(0, size);
            tries = starting_try;
        }

        public bool NewTry(int level)
        {
            tries += 2^(level - 1);
            bool is_winner = tries >= winner;
            
            int overflow = tries - size;
            if (overflow >= 0)
            {
                Logger.LogDebug($"Bag has run out; resetting with {overflow} tries.");
                Reset(overflow);
            }
            return is_winner;
        }

        public override string ToString()
        {
            return $"{tries} tries of {size} maximum, award at {winner} trophy {trophy.GetName()}";
        }
    }

    public static class TrophyBags
    {
        public static Dictionary<Character, Bag> bags = [];

        public static string BagToString()
        {
            return string.Join("\n", bags.Select(res => $"{res.Key.GetName()}:\t{res.Value}"));
        }

        public static void Regenerate()
        {
            // https://github.com/Valheim-Modding/Jotunn/blob/41cd418939707f9ae925e8c2927c195c1af0c906/JotunnDoc/Docs/CharacterDoc.cs#L35
            List<GameObject> allPrefabs = [];
            allPrefabs.AddRange(ZNetScene.instance.m_nonNetViewPrefabs);
            allPrefabs.AddRange(ZNetScene.instance.m_prefabs);

            foreach (GameObject character in allPrefabs.Where(x => x.GetComponent<Character>() != null).OrderBy(x => x.name))
            {
                if (character.TryGetComponent<CharacterDrop>(out var chardrop)) {
                    foreach (CharacterDrop.Drop drop in chardrop.m_drops)
                    {
                        drop.m_prefab.TryGetComponent<ItemDrop>(out var component);
                        if (component != null && component.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
                        {
                            bags.Add(character.GetComponent<Character>(), new Bag(drop.m_chance, drop.m_prefab));
                        }
                    }
                }
            }
            Logger.LogDebug("Trophy bags (re)generated!:\n" + BagToString());
        }
    }
        
    private void Awake()
    {
        // Plugin startup logic
        harmony = new Harmony(Info.Metadata.GUID);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} has arrived!");
    }

    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
    private static class ZNetScene_Awake_Patch
    {
        private static void Postfix()
        {
            Logger.LogDebug("ZNetScene.Awake.Postfix");
            TrophyBags.Regenerate();
        }
    }

    [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
    private static class CharacterDrop_GenerateDropList_Patch
    {
        private static void Postfix(CharacterDrop __instance, ref List<KeyValuePair<GameObject, int>> __result)
        {
            Character this_char = __instance.m_character;
            Logger.LogDebug($"CharacterDrop.GenerateDropList.Postfix for {this_char.GetName()}");
            
            // removing trophies awared by the build-in logic
            foreach (KeyValuePair<GameObject, int> drop in __result.ToList())
            {
                if (drop.Key != null && drop.Key.TryGetComponent<ItemDrop>(out var component) && component.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
                {
                    Logger.LogInfo($"Found trophy {component.m_itemData.m_shared.m_name} in drop table from built-in logic; removing it.");
                    __result.Remove(drop);
                }
            }

            // adding trophies awared by bag randomizer logic
            foreach (CharacterDrop.Drop drop in __instance.m_drops)
            {
                if (drop == null)
                {
                    Logger.LogInfo("Got a null in the drop list. CharacterDrops.m_drops must be List<Drop?>");
                    break;
                }
                if (drop.m_prefab.TryGetComponent<ItemDrop>(out var component) && component.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
                {
                    Logger.LogInfo($"Event's drop list had a trophy-type entry; associated bag for {__instance.m_character.GetName()} has {TrophyBags.bags[this_char]}");
                    bool success = TrophyBags.bags[this_char].NewTry(this_char.GetLevel());
                    if (success)
                    {
                        // These vanilla checks are kept
                        int how_many = drop.m_dontScale ? UnityEngine.Random.Range(drop.m_amountMin, drop.m_amountMax) : Game.instance.ScaleDrops(drop.m_prefab, drop.m_amountMin, drop.m_amountMax);
                        if (drop.m_levelMultiplier && !drop.m_levelMultiplierNotAmount)
                        {
                            how_many *= (!__instance.m_character) ? 1 : Mathf.Max(1, (int)Mathf.Pow(2f, __instance.m_character.GetLevel() - 1));
                        }
                        if (drop.m_onePerPlayer)
                        {
                            how_many = ZNet.instance.GetNrOfPlayers();
                        }
                        if (how_many > 100)
                        {
                            how_many = 100;
                        }
                        
                        // Inject into the DropList that's about to be spawned
                        Logger.LogInfo($"Success! Adding {how_many} copies of {TrophyBags.bags[this_char].trophy.GetName()} to dropping list.");
                        __result.Add(new KeyValuePair<GameObject, int>(TrophyBags.bags[this_char].trophy, how_many));
                    }
                }
            }
        }
    }
}
