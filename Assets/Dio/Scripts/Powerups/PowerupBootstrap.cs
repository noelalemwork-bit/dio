using UnityEngine;
using Dio.Powerups.Effects;

namespace Dio.Powerups
{
    /// Single point where powerup effects + their networked prefabs are
    /// registered. Drop this on a GameObject in the Main scene with prefab
    /// fields wired (the editor builder fills them in for you).
    public class PowerupBootstrap : MonoBehaviour
    {
        [Header("Networked obstacle prefabs")]
        public GameObject bananaPrefab;
        public GameObject oilSlickPrefab;
        public GameObject greenShellPrefab;
        public GameObject blueShellPrefab;
        public GameObject bobombPrefab;
        public GameObject tornadoPrefab;
        public GameObject powerupBoxPrefab;

        public GameObject[] AllNetworkedPrefabs => new[]
        {
            bananaPrefab, oilSlickPrefab, greenShellPrefab, blueShellPrefab,
            bobombPrefab, tornadoPrefab, powerupBoxPrefab,
        };

        void Awake()
        {
            PowerupRegistry.Clear();

            // Self-buffs.
            PowerupRegistry.Register(PowerupKind.Boost, new BoostEffect());
            PowerupRegistry.Register(PowerupKind.TripleBoost, new TripleBoostEffect());
            PowerupRegistry.Register(PowerupKind.Star, new StarEffect());

            // Global.
            PowerupRegistry.Register(PowerupKind.Lightning, new LightningEffect());

            // Static hazards.
            PowerupRegistry.Register(PowerupKind.Banana, new BananaEffect(), bananaPrefab);
            PowerupRegistry.Register(PowerupKind.OilSlick, new OilSlickEffect(), oilSlickPrefab);

            // Projectiles.
            PowerupRegistry.Register(PowerupKind.GreenShell, new GreenShellEffect(), greenShellPrefab);
            PowerupRegistry.Register(PowerupKind.BlueShell, new BlueShellEffect(), blueShellPrefab);
            PowerupRegistry.Register(PowerupKind.Bobomb, new BobombEffect(), bobombPrefab);

            // Wandering.
            PowerupRegistry.Register(PowerupKind.Tornado, new TornadoEffect(), tornadoPrefab);
        }
    }
}
