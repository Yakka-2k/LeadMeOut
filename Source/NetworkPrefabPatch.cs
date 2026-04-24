using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace LeadMeOut
{
    [HarmonyPatch(typeof(NetworkManager))]
    internal static class NetworkPrefabPatch
    {
        private static readonly string MOD_GUID = MyPluginInfo.PLUGIN_GUID;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(NetworkManager.SetSingleton))]
        private static void RegisterPrefab()
        {
            var prefab = new GameObject(MOD_GUID + " Prefab");
            prefab.hideFlags |= HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(prefab);
            var networkObject = prefab.AddComponent<NetworkObject>();
            var fieldInfo = typeof(NetworkObject).GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.NonPublic);
            fieldInfo!.SetValue(networkObject, GetHash(MOD_GUID));
            NetworkManager.Singleton.PrefabHandler.AddNetworkPrefab(prefab);
            return;

            static uint GetHash(string value)
            {
                return value?.Aggregate(17u, (current, c) => unchecked((current * 31) ^ c)) ?? 0u;
            }
        }
    }
}
