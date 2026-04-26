using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Dio.Level.EditorTools
{
    /// Cool-but-deterministic two-word level names. The Tools/Dio/New Level
    /// menu uses `Generate` to stamp a unique name onto a fresh asset; the
    /// backfill menu walks every existing LevelData and renames its asset
    /// file to its old `displayName` value (if set) or a freshly generated
    /// two-word combo so every level is identified by FILENAME — the only
    /// identity that's stable across machines (filenames are version-
    /// controlled, .meta GUIDs and runtime indexes are not).
    public static class LevelNaming
    {
        // Adjective + noun pools chosen for "racing-track flavour" — short,
        // evocative, all single tokens so concatenation produces a clean
        // CamelCase asset filename.
        static readonly string[] Adjectives = {
            "Brave", "Bold", "Cosmic", "Crimson", "Dawn", "Dusk", "Echo", "Ember",
            "Frosted", "Gilded", "Glacier", "Golden", "Hidden", "Iron", "Jade", "Lunar",
            "Mystic", "Nebula", "Onyx", "Pulse", "Rapid", "Royal", "Silent", "Solar",
            "Stellar", "Storm", "Sunset", "Thunder", "Tidal", "Velvet", "Verdant", "Wild",
            "Whisper", "Zenith",
        };

        static readonly string[] Nouns = {
            "Arc", "Atlas", "Bay", "Bluff", "Canyon", "Citadel", "Cove", "Crest",
            "Crown", "Dunes", "Falls", "Fjord", "Forest", "Glade", "Gorge", "Harbor",
            "Heights", "Highway", "Hollow", "Isle", "Lagoon", "Loop", "Meadow", "Mesa",
            "Oasis", "Orbit", "Peak", "Reef", "Ridge", "River", "Sands", "Shore",
            "Spire", "Trail", "Valley",
        };

        /// Generate a name that doesn't collide with any existing LevelData
        /// asset (case-insensitive). Up to 64 attempts; if every random
        /// combo is taken (extremely unlikely with ~34×35 = ~1190 combos)
        /// we suffix a numeric tag.
        public static string Generate()
        {
            var existing = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var lvl in LevelLibrary.ScanLocal())
                if (lvl != null && !string.IsNullOrEmpty(lvl.name)) existing.Add(lvl.name);

            for (int attempt = 0; attempt < 64; attempt++)
            {
                string candidate = Adjectives[Random.Range(0, Adjectives.Length)]
                                 + Nouns[Random.Range(0, Nouns.Length)];
                if (!existing.Contains(candidate)) return candidate;
            }
            // Fallback — vanishingly rare in practice.
            return $"Track{System.DateTime.Now:HHmmss}";
        }

        /// One-shot migration tool: rename every LevelData under
        /// `Assets/Dio/Levels/` to a stable, unique asset filename. Each
        /// level keeps its old displayName as the new filename when one is
        /// authored (so existing tracks don't lose their names); otherwise
        /// we stamp a fresh two-word combo.
        ///
        /// LevelData no longer has a displayName field — to read the
        /// legacy value we re-parse the YAML directly. After the rename
        /// the YAML still contains the dead displayName key; Unity ignores
        /// unknown fields so this is harmless. A future cleanup pass can
        /// strip them with another migration if it bothers anyone.
        [MenuItem("Tools/Dio/Build/Backfill Level Names From DisplayName")]
        public static void BackfillFromDisplayName()
        {
            const string LevelsDir = "Assets/Dio/Levels";
            var guids = AssetDatabase.FindAssets("t:LevelData", new[] { LevelsDir });
            int renamed = 0, kept = 0;
            // Sort by current asset path so the "loop until unique" logic
            // is deterministic across runs / machines.
            var paths = new List<string>(guids.Length);
            foreach (var g in guids) paths.Add(AssetDatabase.GUIDToAssetPath(g));
            paths.Sort();

            // Track the names we've already claimed within this run so two
            // levels with the same authored displayName don't collide.
            var taken = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                var lvl = AssetDatabase.LoadAssetAtPath<LevelData>(path);
                if (lvl == null) continue;
                string desired = TryReadLegacyDisplayName(path);
                if (string.IsNullOrWhiteSpace(desired)) desired = lvl.name;
                desired = SanitiseFilename(desired);
                if (string.IsNullOrEmpty(desired)) desired = Generate();

                // Resolve uniqueness: if `desired` is taken, append _2, _3, …
                string final = desired;
                int suffix = 2;
                while (taken.Contains(final))
                {
                    final = $"{desired}_{suffix++}";
                }
                taken.Add(final);

                if (lvl.name != final)
                {
                    var err = AssetDatabase.RenameAsset(path, final);
                    if (!string.IsNullOrEmpty(err))
                    {
                        Debug.LogWarning($"[LevelNaming] rename '{lvl.name}' → '{final}' failed: {err}");
                        continue;
                    }
                    renamed++;
                }
                else kept++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[LevelNaming] Backfill complete: {renamed} renamed, {kept} unchanged.");
        }

        // Lightweight YAML scrape — pulls the legacy `displayName: …` line
        // off disk without needing the C# field to still exist. Returns
        // empty when the file doesn't have one.
        static string TryReadLegacyDisplayName(string assetPath)
        {
            try
            {
                string full = System.IO.Path.GetFullPath(assetPath);
                if (!System.IO.File.Exists(full)) return "";
                using (var sr = new System.IO.StreamReader(full))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        var t = line.TrimStart();
                        const string Key = "displayName:";
                        if (!t.StartsWith(Key)) continue;
                        return t.Substring(Key.Length).Trim().Trim('"', '\'');
                    }
                }
            }
            catch { }
            return "";
        }

        // Strip filesystem-unsafe characters and collapse whitespace so an
        // arbitrary user-authored displayName ("Test Level: v2!") becomes a
        // valid asset filename ("TestLevelv2").
        static string SanitiseFilename(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == '_' || c == '-') sb.Append(c);
                // skip everything else (spaces, punctuation)
            }
            return sb.ToString();
        }
    }
}
