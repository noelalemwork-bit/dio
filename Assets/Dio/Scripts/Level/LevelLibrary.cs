using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Dio.Level
{
    /// Per-peer scan of locally available LevelData assets. Editor uses
    /// AssetDatabase under `Assets/Dio/Levels/`; built players use
    /// `Resources.LoadAll<LevelData>("Levels")`. Either way the result is
    /// the SAME set of LevelData instances — one entry per asset on disk,
    /// keyed by their `displayName` (auto-filled from the file name when
    /// the field is empty).
    ///
    /// This is intentionally read-at-runtime, not baked into a prefab at
    /// build-menu time. Restarting the editor / hot-reloading the player
    /// picks up new levels without rerunning a build step.
    public static class LevelLibrary
    {
        const string LevelsAssetFolder = "Assets/Dio/Levels";
        const string LevelsResourcesFolder = "Levels";

        /// Scan and return every LevelData visible to the local peer. Each
        /// asset is returned ONCE; ordering is alphabetical by displayName
        /// for stable lobby UI. The displayName is normalised (file name
        /// fallback) before being returned so callers never see empty
        /// strings.
        public static List<LevelData> ScanLocal()
        {
            var list = new List<LevelData>();

#if UNITY_EDITOR
            string[] guids = AssetDatabase.FindAssets("t:LevelData", new[] { LevelsAssetFolder });
            for (int i = 0; i < guids.Length; i++)
            {
                string p = AssetDatabase.GUIDToAssetPath(guids[i]);
                var lvl = AssetDatabase.LoadAssetAtPath<LevelData>(p);
                if (lvl != null) list.Add(lvl);
            }
#else
            var loaded = Resources.LoadAll<LevelData>(LevelsResourcesFolder);
            list.AddRange(loaded);
#endif

            for (int i = 0; i < list.Count; i++) NormaliseDisplayName(list[i]);
            list.Sort((a, b) => string.Compare(
                string.IsNullOrEmpty(a.displayName) ? a.name : a.displayName,
                string.IsNullOrEmpty(b.displayName) ? b.name : b.displayName,
                System.StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// Editor uniqueness check: returns true when `candidate`'s
        /// displayName collides with another LevelData asset's displayName
        /// (case-insensitive). The level being saved is excluded from the
        /// comparison so saving an already-named level doesn't self-conflict.
        public static bool IsDisplayNameTaken(LevelData candidate, string proposedName)
        {
            if (string.IsNullOrWhiteSpace(proposedName)) return false;
            var others = ScanLocal();
            for (int i = 0; i < others.Count; i++)
            {
                if (others[i] == candidate) continue;
                string n = string.IsNullOrEmpty(others[i].displayName) ? others[i].name : others[i].displayName;
                if (string.Equals(n, proposedName, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// Auto-fill displayName from the asset's file name when missing.
        /// Lets old levels (saved before the field existed) round-trip
        /// through the network catalog without designers having to set the
        /// name explicitly.
        public static void NormaliseDisplayName(LevelData lvl)
        {
            if (lvl == null) return;
            if (!string.IsNullOrWhiteSpace(lvl.displayName)) return;
            lvl.displayName = lvl.name; // ScriptableObject.name = file name in editor
        }
    }
}
