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
            // Collect target world positions FIRST so we can broadcast a
            // single Rpc with the full list (cheaper than one Rpc per target).
            var targets = new System.Collections.Generic.List<Vector3>();
            foreach (var ni in NetworkServer.spawned.Values)
            {
                var car = ni.GetComponent<DioCar>();
                if (car == null || car == caster) continue;
                var sm = car.GetComponent<CarStateMachine>();
                if (sm != null && !sm.IsInvincible)
                {
                    sm.Apply(new ShrunkState { Duration = Duration });
                    targets.Add(car.transform.position);
                }
            }

            // Show the visual zap on every peer, including solo-test (empty
            // target list draws a single forward bolt as fallback feedback).
            var holder = caster.GetComponent<PowerupHolder>();
            if (holder != null)
                holder.RpcShowLightning(caster.transform.position, targets.ToArray());
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

    public class BobombEffect : SpawnPrefabBehindEffect
    {
        public override PowerupKind Kind => PowerupKind.Bobomb;
        public BobombEffect() { backOffset = 3f; }
        public override void Activate(DioCar owner) => Spawn(owner, PowerupRegistry.GetPrefab(Kind));
    }

    // ---------- Wandering obstacle ----------

    public class TornadoEffect : PowerupEffect
    {
        public override PowerupKind Kind => PowerupKind.Tornado;
        [Tooltip("How far ahead of the caster the tornado is spat.")]
        public float forwardOffset = 14f;

        public override void Activate(DioCar owner)
        {
            var prefab = PowerupRegistry.GetPrefab(Kind);
            if (prefab == null) { Debug.LogWarning($"[Dio] Tornado has no prefab."); return; }
            // Spawn ahead of the caster on the planet surface. TornadoObstacle
            // re-snaps to the surface in OnStartServer so we don't have to be
            // exact about the radial component here.
            Vector3 pos = owner.transform.position + owner.transform.forward * forwardOffset;
            var go = Object.Instantiate(prefab, pos, owner.transform.rotation);
            var t = go.GetComponent<TornadoObstacle>();
            if (t != null)
            {
                var planetGo = Dio.Level.RaceBootstrap.CurrentPlanet;
                if (planetGo != null)
                {
                    t.planet = planetGo.transform;
                    var lvl = Dio.Level.RaceBootstrap.CurrentLevel;
                    if (lvl != null) t.planetRadius = lvl.planetRadius;
                }
            }
            var ob = go.GetComponent<Obstacle>();
            if (ob != null) ob.ownerConnectionId = owner.connectionToClient != null ? owner.connectionToClient.connectionId : -1;
            NetworkServer.Spawn(go);
        }
    }
}
