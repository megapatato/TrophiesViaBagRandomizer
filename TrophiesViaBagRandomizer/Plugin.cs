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
                Logger.LogError($"Got a negative starting_try of {starting_try}! Resetting it to 0.");
                starting_try = 0;
            }
            winner = UnityEngine.Random.Range(0, size);
            tries = starting_try;
            Logger.LogDebug($"Bag.Reset! {this}");
        }

        public bool NewTry(int level)
        {
            int ending_tries = tries + (int)Mathf.Pow(2, level - 1);
            bool is_winner = (tries <= winner) && (winner < ending_tries);
            Logger.LogDebug($"Bag.NewTry! tries {tries}; level {level}; ending_tries {ending_tries}; winner at {winner}; is winner {is_winner}");
            tries = ending_tries;

            int overflow = ending_tries - size;
            if (overflow >= 0)
            {
                Logger.LogDebug($"Bag has run out; resetting with {overflow} initial tries.");
                Reset(overflow);
            }
            
            return is_winner;
        }

        public override string ToString()
        {
            return $"{tries} tries of {size} maximum, award at {winner}:\t{trophy.GetName()}";
        }
    }

    public static class TrophyBags
    {
        public static Dictionary<string, Bag> bags = [];

        public static string BagsToString()
        {
            return string.Join("\n", bags.Select(res => $"{res.Key}:\t{res.Value}"));
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
                            bags.Add(character.GetComponent<Character>().GetName(), new Bag(drop.m_chance, drop.m_prefab));
                        }
                    }
                }
            }
            Logger.LogDebug("Trophy bags (re)generated!:\n" + BagsToString());
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
            string clone_name = __instance.m_character.GetName();
            if (!clone_name.Contains("(Clone)"))
            {
                Logger.LogError($"Character instance named {clone_name} does not contain the <(Clone)> substring!");
                return;
            }

            string prefab_name = clone_name.Split("(Clone)")[0];
            Logger.LogDebug($"CharacterDrop.GenerateDropList.Postfix for {prefab_name}");
            
            // removing trophies awared by the build-in logic
            foreach (KeyValuePair<GameObject, int> drop in __result.ToList())
            {
                if (drop.Key != null && drop.Key.TryGetComponent<ItemDrop>(out var component) && component.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
                {
                    Logger.LogDebug($"Found trophy {component.m_itemData.m_shared.m_name} in drop table of {prefab_name} from built-in logic; removing it.");
                    __result.Remove(drop);
                }
            }

            // adding trophies awared by bag randomizer logic
            foreach (CharacterDrop.Drop drop in __instance.m_drops)
            {
                if (drop == null)
                {
                    Logger.LogInfo("Got a null in the drop list. CharacterDrops.m_drops must be List<Drop?>");
                    continue;
                }
                if (drop.m_prefab.TryGetComponent<ItemDrop>(out var component) && component.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trophy)
                {
                    Logger.LogDebug($"Drop list had a trophy-type entry; {prefab_name} ->\t{TrophyBags.bags[prefab_name]}");
                    bool success = TrophyBags.bags[prefab_name].NewTry(__instance.m_character.GetLevel());
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
                        Logger.LogDebug($"Draw success! Adding {how_many} cop" + (how_many == 1 ? "y" : "ies") + $" of {TrophyBags.bags[prefab_name].trophy.GetName()} to dropping list.");
                        __result.Add(new KeyValuePair<GameObject, int>(TrophyBags.bags[prefab_name].trophy, how_many));
                    }
                }
            }
        }
    }
}
