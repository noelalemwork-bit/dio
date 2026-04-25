using System.Collections.Generic;
using UnityEngine;

namespace Dio.Powerups
{
    /// Maps PowerupKind → PowerupEffect (concrete server logic) + an optional
    /// prefab to spawn (shells, bombs, etc.). Populated at runtime by
    /// PowerupBootstrap so we don't need a ScriptableObject database for the
    /// first milestone.
    public static class PowerupRegistry
    {
        static readonly Dictionary<PowerupKind, PowerupEffect> _effects = new();
        static readonly Dictionary<PowerupKind, GameObject> _prefabs = new();
        static readonly List<PowerupKind> _availableForBox = new();

        public static void Register(PowerupKind kind, PowerupEffect effect, GameObject prefab = null, bool boxSelectable = true)
        {
            _effects[kind] = effect;
            if (prefab != null) _prefabs[kind] = prefab;
            if (boxSelectable && !_availableForBox.Contains(kind)) _availableForBox.Add(kind);
        }

        public static bool TryGetEffect(PowerupKind kind, out PowerupEffect effect) => _effects.TryGetValue(kind, out effect);

        public static GameObject GetPrefab(PowerupKind kind) => _prefabs.TryGetValue(kind, out var p) ? p : null;

        public static PowerupKind RandomFromBox()
        {
            if (_availableForBox.Count == 0) return PowerupKind.None;
            return _availableForBox[Random.Range(0, _availableForBox.Count)];
        }

        public static void Clear()
        {
            _effects.Clear();
            _prefabs.Clear();
            _availableForBox.Clear();
        }
    }
}
