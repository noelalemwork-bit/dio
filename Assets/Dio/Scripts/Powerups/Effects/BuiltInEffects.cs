using Mirror;
using UnityEngine;
using Dio.Player;
using Dio.Powerups.States;
using Dio.Level.Obstacles;

namespace Dio.Powerups.Effects
{
    // ---------- Self-buffs ----------

    public class BoostEffect : PowerupEffect
    {
        public override PowerupKind Kind => PowerupKind.Boost;
        public float Duration = 1.4f;

        public override void Activate(DioCar owner)
        {
            var sm = owner.GetComponent<CarStateMachine>();
            if (sm != null) sm.Apply(new SpeedBoostState { Duration = Duration });
        }
    }

    public class TripleBoostEffect : PowerupEffect
    {
        public override PowerupKind Kind => PowerupKind.TripleBoost;
        // Activation is the same as BoostEffect; the holder dispenses charges.
        public override void Activate(DioCar owner)
        {
            var sm = owner.GetComponent<CarStateMachine>();
            if (sm != null) sm.Apply(new SpeedBoostState { Duration = 1.0f });
        }
    }

    public class StarEffect : PowerupEffect
    {
        public override PowerupKind Kind => PowerupKind.Star;
        public float Duration = 5f;
        public override void Activate(DioCar owner)
        {
            var sm = owner.GetComponent<CarStateMachine>();
            if (sm != null) sm.Apply(new StarState { Duration = Duration });
        }
    }

    // ---------- Global ----------

    public class LightningEffect : PowerupEffect
    {
        public override PowerupKind Kind => PowerupKind.Lightning;
        public float Duration = 4f;

        public override void Activate(DioCar caster)
        {
            foreach (var ni in NetworkServer.spawned.Values)
            {
                var car = ni.GetComponent<DioCar>();
                if (car == null || car == caster) continue;
                var sm = car.GetComponent<CarStateMachine>();
                if (sm != null && !sm.IsInvincible)
                    sm.Apply(new ShrunkState { Duration = Duration });
            }
        }
    }

    // ---------- Spawned ground hazards ----------

    public abstract class SpawnPrefabBehindEffect : PowerupEffect
    {
        public float backOffset = 2.5f;
        protected GameObject Spawn(DioCar owner, GameObject prefab)
        {
            if (prefab == null) { Debug.LogWarning($"[Dio] {Kind} has no registered prefab."); return null; }
            Vector3 pos = owner.transform.position - owner.transform.forward * backOffset;
            var go = Object.Instantiate(prefab, pos, owner.transform.rotation);
            var ob = go.GetComponent<Obstacle>();
            if (ob != null) ob.ownerConnectionId = owner.connectionToClient != null ? owner.connectionToClient.connectionId : -1;
            NetworkServer.Spawn(go);
            return go;
        }
    }

    public class BananaEffect : SpawnPrefabBehindEffect
    {
        public override PowerupKind Kind => PowerupKind.Banana;
        public override void Activate(DioCar owner) => Spawn(owner, PowerupRegistry.GetPrefab(Kind));
    }

    public class OilSlickEffect : SpawnPrefabBehindEffect
    {
        public override PowerupKind Kind => PowerupKind.OilSlick;
        public override void Activate(DioCar owner) => Spawn(owner, PowerupRegistry.GetPrefab(Kind));
    }

    // ---------- Spawned forward projectiles ----------

    public abstract class SpawnPrefabForwardEffect : PowerupEffect
    {
        public float forwardOffset = 3.0f;
        protected GameObject Spawn(DioCar owner, GameObject prefab)
        {
            if (prefab == null) { Debug.LogWarning($"[Dio] {Kind} has no registered prefab."); return null; }
            Vector3 pos = owner.transform.position + owner.transform.forward * forwardOffset;
            var go = Object.Instantiate(prefab, pos, owner.transform.rotation);
            var ob = go.GetComponent<Obstacle>();
            if (ob != null) ob.ownerConnectionId = owner.connectionToClient != null ? owner.connectionToClient.connectionId : -1;
            NetworkServer.Spawn(go);
            return go;
        }
    }

    public class GreenShellEffect : SpawnPrefabForwardEffect
    {
        public override PowerupKind Kind => PowerupKind.GreenShell;
        public override void Activate(DioCar owner) => Spawn(owner, PowerupRegistry.GetPrefab(Kind));
    }

    public class BlueShellEffect : SpawnPrefabForwardEffect
    {
        public override PowerupKind Kind => PowerupKind.BlueShell;
        public override void Activate(DioCar owner) => Spawn(owner, PowerupRegistry.GetPrefab(Kind));
    }

    public class BobombEffect : SpawnPrefabForwardEffect
    {
        public override PowerupKind Kind => PowerupKind.Bobomb;
        public override void Activate(DioCar owner) => Spawn(owner, PowerupRegistry.GetPrefab(Kind));
    }

    // ---------- Wandering obstacle ----------

    public class TornadoEffect : PowerupEffect
    {
        public override PowerupKind Kind => PowerupKind.Tornado;
        public override void Activate(DioCar owner)
        {
            var prefab = PowerupRegistry.GetPrefab(Kind);
            if (prefab == null) { Debug.LogWarning($"[Dio] Tornado has no prefab."); return; }
            // Place 30 m ahead on the surface, deterministic basis.
            Vector3 pos = owner.transform.position + owner.transform.forward * 30f;
            var go = Object.Instantiate(prefab, pos, owner.transform.rotation);
            var t = go.GetComponent<TornadoObstacle>();
            if (t != null)
            {
                t.circleCenterDir = pos.normalized;
                var planetGo = Dio.Level.RaceBootstrap.CurrentPlanet;
                if (planetGo != null) t.planet = planetGo.transform;
            }
            var ob = go.GetComponent<Obstacle>();
            if (ob != null) ob.ownerConnectionId = owner.connectionToClient != null ? owner.connectionToClient.connectionId : -1;
            NetworkServer.Spawn(go);
        }
    }
}
