using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace Dio.Level
{
    /// Mirror reader/writer extensions so a `LevelData` ScriptableObject can
    /// be transmitted by VALUE inside a NetworkMessage — not by asset GUID
    /// (which doesn't match across clones with stripped meta files).
    ///
    /// Mirror finds these via reflection on type signature; any NetworkMessage
    /// or SyncVar of type `LevelData` will use these automatically. We keep
    /// the wire format flat + explicit so a future schema change is obvious
    /// and additive (bump a version field, extend the reader/writer).
    public static class LevelDataNetworkExtensions
    {
        // Wire bumped to 3: per-anchor `guardRailFloating` flag (controls the
        // skirt of every outgoing bezier — short floating rail vs. full skirt
        // down to the planet surface).
        const byte WireVersion = 3;

        public static void WriteLevelData(this NetworkWriter writer, LevelData level)
        {
            if (level == null) { writer.WriteByte(0); return; }
            writer.WriteByte(WireVersion);
            writer.WriteString(level != null ? (level.name ?? string.Empty) : string.Empty);
            writer.WriteFloat(level.circumference);
            writer.WriteFloat(level.trackRatio);
            writer.WriteFloat(level.trackWidth);

            int n = level.points != null ? level.points.Count : 0;
            writer.WriteInt(n);
            for (int i = 0; i < n; i++)
            {
                var p = level.points[i];
                writer.WriteVector3(p.directionFromCenter);
                writer.WriteFloat(p.bank);
                writer.WriteVector3(p.inHandle);
                writer.WriteVector3(p.outHandle);
                writer.WriteFloat(p.yOffset);
                writer.WriteBool(p.guardRailFloating);
                int edges = p.next != null ? p.next.Count : 0;
                writer.WriteInt(edges);
                for (int k = 0; k < edges; k++) writer.WriteInt(p.next[k]);
            }
        }

        public static LevelData ReadLevelData(this NetworkReader reader)
        {
            byte ver = reader.ReadByte();
            if (ver == 0) return null;
            if (ver != WireVersion)
            {
                Debug.LogError($"[LevelDataSerializer] unsupported wire version {ver} (expected {WireVersion}).");
                return null;
            }
            var level = ScriptableObject.CreateInstance<LevelData>();
            // Stamp the loaded ScriptableObject's runtime name from the
            // wire payload — that's what every catalog key + lookup uses.
            level.name = reader.ReadString();
            level.circumference = reader.ReadFloat();
            level.trackRatio = reader.ReadFloat();
            level.trackWidth = reader.ReadFloat();

            int n = reader.ReadInt();
            level.points = new List<TrackPoint>(n);
            for (int i = 0; i < n; i++)
            {
                var p = new TrackPoint();
                p.directionFromCenter = reader.ReadVector3();
                p.bank = reader.ReadFloat();
                p.inHandle = reader.ReadVector3();
                p.outHandle = reader.ReadVector3();
                p.yOffset = reader.ReadFloat();
                p.guardRailFloating = reader.ReadBool();
                int edges = reader.ReadInt();
                p.next = new List<int>(edges);
                for (int k = 0; k < edges; k++) p.next.Add(reader.ReadInt());
                level.points.Add(p);
            }
            return level;
        }
    }
}
