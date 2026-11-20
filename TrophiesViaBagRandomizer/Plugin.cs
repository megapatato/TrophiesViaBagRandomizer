using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

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
        public int Tries {get; set; }

        /// <summary>
        /// Size of the bag; the maximum number of tries before a trophy is awarded.
        /// </summary>
        public int Size {get; set; }

        /// <summary>
        /// The per-event probability as stated by the IronGate; used in ProgressFrom.
        /// </summary>
        public float Probability {get; set; }

        /// <summary>
        /// The try that will award the trophy.
        /// </summary>
        public int Winner {get; set; }

        /// <summary>
        /// The thing to spawn; injected back into the GenerateDropList List<KeyValuePair<GameObject, int>> output.
        /// </summary>
        public GameObject Trophy {get; set; }

        /// <summary>
        /// Transforms a decimal probability into an integer bag size. For cases where there is no single integer matching size (e.g. 15%), a weighted size is returned (e.g. 6 or 7) that approximates IronGate's intended probability with enough sampling.
        /// </summary>
        private int ProbToSize(float probability)
        {
            float prob_reciprocal = 1/probability ;
            int proto_size = Mathf.FloorToInt(prob_reciprocal);
            if ( prob_reciprocal != Mathf.Floor(prob_reciprocal) ) {
                float threshhold = prob_reciprocal - proto_size;
                proto_size += (UnityEngine.Random.Range(0, 1) <= threshhold) ? 0 : 1;
            }
            return proto_size;
        }

        public Bag(float probability, GameObject trophy_drop)
        {
            
            Size = ProbToSize(probability);
            Probability = probability;
            Trophy = trophy_drop;
            Reset();
        }

        public Bag(int tries, float probability, int winner, GameObject trophy_drop)
        {
            Tries = tries;
            Size = ProbToSize(probability);
            Probability = probability;
            Winner = winner;
            Trophy = trophy_drop;
        }

        private void Reset(int starting_tries = 0)
        {
            if (starting_tries < 0)
            {
                Logger.LogError($"Bag.Reset got a negative starting_try of {starting_tries}! Using 0 instead.");
                starting_tries = 0;
            }
            if (starting_tries >= Size)
            {
                Winner = Size - 1;
                Tries = starting_tries % Size;
                Logger.LogDebug($"Bag.Reset overflowed past size of {Size} with {starting_tries}! Assigning {Winner} as winner and initializing bag at {Tries}");
            } else {
                Winner = UnityEngine.Random.Range(starting_tries, Size);
                Tries = starting_tries;
            }
            Logger.LogDebug($"Bag.Reset! {PrettyPrint()}");
        }

        public bool NewTry(int enemy_level)
        {
            int ending_tries = Tries + (int)Mathf.Pow(2, enemy_level - 1);
            bool awarding = (Tries <= Winner) && (Winner < ending_tries);
            Logger.LogDebug($"Bag.NewTry update: tries {Tries}; level {enemy_level}; ending_tries {ending_tries}; winner at {Winner}; awarding {awarding}");
            Tries = ending_tries;

            int overflow = ending_tries - Size;
            if (overflow >= 0)
            {
                Logger.LogDebug($"Bag.NewTry this bag has run out; resetting with {overflow} initial tries.");
                Reset(overflow);
            }
            
            return awarding;
        }

        public string PrettyPrint()
        {
            return $"{Tries} tries of {Size} maximum, award at {Winner}:\t{Trophy.GetName()}";
        }

        public bool ProgressFrom(Bag other_bag)
        {
            if (Probability == other_bag.Probability) {
                Tries = other_bag.Tries;
                Winner = other_bag.Winner;
                return true;
            } 
            else
            {
                return false;
            }
        }
    }

    public static class TrophyBags
    {
        /// <summary>
        /// Dictionary to map an enemy character's name (akin to its type) to the relevant bag-type object tracking progress towards a trophy.
        /// </summary>
        public static Dictionary<string, Bag> bags = [];

        /// <summary>
        /// Dictionary to map trophy's string name to the prefab GameObject it relates to.
        /// </summary>
        // public static Dictionary<string, GameObject> TrophyNameToGameObject = [];

        public static string PrettyPrint()
        {
            return string.Join("\n", bags.Select(res => $"{res.Key}:\t{res.Value}"));
        }

        public static void UpdateFromZNet()
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
                            string enemy_name = character.GetComponent<Character>().GetName();
                            Bag fresh_bag = new(drop.m_chance, drop.m_prefab);

                            if (bags.ContainsKey(enemy_name))
                            {
                                Bag stale_bag = bags[enemy_name];
                                fresh_bag.ProgressFrom(stale_bag);  // keeps only Tries & Winner, if appropriate
                                bags[enemy_name] = fresh_bag;
                            }
                            else
                            {
                                bags.Add(enemy_name, fresh_bag);
                            }
                        }
                    }
                }
            }
        }

        public static void ProgressFrom(ZPackage payload)
        {
            try
            {
                int NumSavedBags = payload.ReadInt();
                for (int i = 0; i < NumSavedBags; i++)
                {
                    string enemy_char = payload.ReadString();
                    int this_tries = payload.ReadInt();
                    float this_probability = payload.ReadSingle();
                    int this_winner = payload.ReadInt();
                    GameObject this_trophy = bags[enemy_char].Trophy;
                    Bag this_bag = new(this_tries, this_probability, this_winner, this_trophy);
                    bool progressed = bags[enemy_char].ProgressFrom(this_bag);
                    if (progressed)
                    {
                        Logger.LogDebug($"Updated from save-game: {bags[enemy_char].PrettyPrint()}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error loading ZPackage: {ex.Message}");
            }
        }

        public static ZPackage ToZPackage()
        {
            ZPackage payload = new();
            payload.Write(bags.Count);
            foreach (string enemy_char in bags.Keys)
            {
                payload.Write(enemy_char);
                payload.Write(bags[enemy_char].Tries);
                payload.Write(bags[enemy_char].Probability);
                payload.Write(bags[enemy_char].Winner);
            }
            return payload;
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
            Logger.LogDebug("ZNetScene_Awake_Patch_Postfix");
            TrophyBags.UpdateFromZNet();
        }
    }


    [HarmonyPatch(typeof(Player), nameof(Player.Start))]
    static class Player_Start_Patch
    {
        static void Postfix(Player __instance)
        {
            Logger.LogDebug("Player_Start_Patch_Postfix");
            ZDOID zDoID = __instance.GetZDOID();
            if (__instance.m_customData.TryGetValue(MyPluginInfo.PLUGIN_NAME, out string data))
            {
                Logger.LogDebug($"<<{MyPluginInfo.PLUGIN_NAME}>> key found in player.m_customData; loading trophy progress");
                ZPackage payload = new(data);
                TrophyBags.ProgressFrom(payload);
            }
            else
            {
                Logger.LogDebug($"<<{MyPluginInfo.PLUGIN_NAME}>> not found in player.m_customData");
            }

        }
    }


    [HarmonyPatch(typeof(Player), nameof(Player.Save))]
    static class Player_Save_Patch
    {
        static void Prefix(Player __instance)
        {
            Logger.LogDebug("Player_Save_Patch_Prefix");
			if (Player.m_localPlayer == null || __instance != Player.m_localPlayer)
			{
				Logger.LogDebug("Player_Save_Patch_Prefix: not the local player, skipping save");
				return;
			}
			ZDOID zDOID = __instance.GetZDOID();
			if (zDOID == ZDOID.None || zDOID != __instance.GetZDOID())
			{
				Logger.LogDebug($"Player_Save_Patch_Prefix: no trustworthy data loaded, skipping save");
				return;
			}
            ZPackage payload = TrophyBags.ToZPackage(); 
            __instance.m_customData[MyPluginInfo.PLUGIN_NAME] = payload.GetBase64();
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
                    Logger.LogDebug($"Drop list for {prefab_name} had a known bag with: {TrophyBags.bags[prefab_name].PrettyPrint()}");
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
                        Logger.LogDebug($"Draw success! Adding {how_many} cop" + (how_many == 1 ? "y" : "ies") + $" of {TrophyBags.bags[prefab_name].Trophy.GetName()} to dropping list.");
                        __result.Add(new KeyValuePair<GameObject, int>(TrophyBags.bags[prefab_name].Trophy, how_many));
                    }
                }
            }
        }
    }
}
