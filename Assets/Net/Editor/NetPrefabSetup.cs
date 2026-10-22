using System.IO;
using System.Reflection;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace RocketWorld.Net.Editor
{
    /// <summary>
    /// Creates Assets/Resources/Net/PlayerNet.prefab (NetworkObject + PlayerNet) so the repository stays code-only, like ProjectSetup does for
    /// the URP asset and materials. Idempotent; runs on every Editor load and before CI builds (BuildScript calls EnsureAll by reflection).
    /// </summary>
    public static class NetPrefabSetup
    {
        public const string PrefabPath = "Assets/Resources/Net/PlayerNet.prefab";

        [InitializeOnLoadMethod]
        static void OnLoad() { EditorApplication.delayCall += () => { if (!Application.isPlaying) EnsureAll(); }; }

        [MenuItem("RocketWorld/Ensure multiplayer setup")]
        public static void EnsureAll()
        {
            Directory.CreateDirectory("Assets/Resources/Net");
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing == null)
            {
                var go = new GameObject("PlayerNet");
                go.AddComponent<NetworkObject>();
                go.AddComponent<PlayerNet>();
                existing = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
                Object.DestroyImmediate(go);
                Debug.Log("[RocketWorld] created " + PrefabPath);
            }
            // NetworkObject normally computes its GlobalObjectIdHash in OnValidate; make sure it is set for a prefab made from code.
            var no = existing != null ? existing.GetComponent<NetworkObject>() : null;
            if (no != null)
            {
                var m = typeof(NetworkObject).GetMethod("GenerateGlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null)
                {
                    try { m.Invoke(no, null); EditorUtility.SetDirty(existing); }
                    catch (System.Exception e) { Debug.LogWarning("[RocketWorld] could not generate the NetworkObject hash: " + e.Message); }
                }
            }
            AssetDatabase.SaveAssets();
        }
    }
}
