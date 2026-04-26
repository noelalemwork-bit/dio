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
    /// keyed by the asset's FILENAME (`level.name`).
    ///
    /// Filename keying is the cross-machine identity — `.meta` GUIDs and
    /// runtime array indexes both diverge between machines, but filenames
    /// don't (they're version-controlled and persisted). Lobby code keys
    /// the runtime catalog by (ownerConnId, level.name); designers rename
    /// a level by renaming the asset file in the project view.
    public static class LevelLibrary
    {
        const string LevelsAssetFolder = "Assets/Dio/Levels";
        const string LevelsResourcesFolder = "Levels";

        /// Scan and return every LevelData visible to the local peer. Each
        /// asset is returned ONCE; ordering is alphabetical by asset
        /// filename for stable lobby UI.
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

            list.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// Editor uniqueness check on a proposed asset filename. Excludes
        /// the level being renamed so renaming an existing level to its
        /// own name isn't reported as a clash.
        public static bool IsNameTaken(LevelData candidate, string proposedName)
        {
            if (string.IsNullOrWhiteSpace(proposedName)) return false;
            var others = ScanLocal();
            for (int i = 0; i < others.Count; i++)
            {
                if (others[i] == candidate) continue;
                if (string.Equals(others[i].name, proposedName, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Backwards-compat helpers for older call sites. NormaliseDisplayName
        // is now a no-op; IsDisplayNameTaken just routes to the filename
        // check. Both will be removed once all referrers are migrated.
        public static void NormaliseDisplayName(LevelData lvl) { /* no-op */ }
        public static bool IsDisplayNameTaken(LevelData candidate, string proposedName)
            => IsNameTaken(candidate, proposedName);
    }
}
