using Mirror;
using UnityEngine;
using Dio.Common;
using Dio.Net;

namespace Dio.Player
{
    /// Networked car. **Client-authority physics** for the local owner:
    /// each peer fully simulates its OWN car (zero local input lag), pushes
    /// position+rotation to the server, server relays to other peers.
    ///
    /// Why: this is a LAN racing game. The server-authoritative round-trip
    /// (Cmd → server sim → NetworkTransform back) added an entire RTT of
    /// input lag for joined players, and on the user's tests the joined
    /// player's car wasn't accelerating at all (only steering visuals fired).
    /// Client authority is the simplest path to "perfect physics, no latency"
    /// per the project's stated goals; no anti-cheat needed for LAN.
    ///
    /// Authority matrix:
    ///   - HOST (isServer && isOwned): full physics locally; NetworkTransform
    ///     pushes its state to other clients (ClientToServer dir, but host
    ///     IS the server, so this is a single hop to other clients).
    ///   - REMOTE OWNER (!isServer && isOwned): full physics locally;
    ///     NetworkTransform sends state to server, which relays to peers.
    ///   - PEER (!isOwned): kinematic; NetworkTransform interpolation drives
    ///     the visible position. No controller simulation.
    ///   - DEDICATED SERVER's view of any car: kinematic; transform updated
    ///     by the owner's NetworkTransform writes. Server still owns triggers
    ///     (powerup boxes, win detection, etc).
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(ArcadeCarController))]
    [RequireComponent(typeof(SphericalGravity))]
    [DefaultExecutionOrder(-100)] // before ArcadeCarController.FixedUpdate
    public class DioCar : NetworkBehaviour
    {
        [SyncVar(hook = nameof(OnColorChanged))] public int colorIndex;
        [SyncVar] public string ownerName = "";

        // Server-set during DioNetworkManager.Update; arc-length the car has
        // traveled along the track. Used by the HUD position panel + win
        // detection. Server-to-client because the server is the only writer.
        [SyncVar] public float progressArc;

        // Anchor indices the car has crossed (one per checkpoint trigger
        // entered). Server-only writer; clients read for display + sorting.
        // Includes the start anchor (added on race start) so x always begins
        // at 1, not 0 — matches the user-visible "1/N" race progress.
        public readonly SyncList<int> crossedCheckpoints = new SyncList<int>();

        // y in the (x/y) HUD display: the player's x plus the minimum number
        // of additional checkpoints to reach the finish from their last
        // crossed checkpoint along the level graph. Server publishes,
        // clients render.
        [SyncVar] public int checkpointsToFinishY;

        // Convenience for rendering and ranking.
        public int CrossedCount => crossedCheckpoints != null ? crossedCheckpoints.Count : 0;

        [Server]
        public void ServerCheckpointHit(int anchorIndex)
        {
            if (crossedCheckpoints == null) return;
            if (crossedCheckpoints.Contains(anchorIndex)) return;
            crossedCheckpoints.Add(anchorIndex);

            // Recompute y from the latest crossed set + level graph. The
            // server is the only writer of this SyncVar.
            var lvl = Dio.Level.RaceBootstrap.CurrentLevel;
            if (lvl != null)
            {
                int last = anchorIndex;
                int finish = lvl.points.Count - 1;
                int dist = Dio.Level.LevelGraph.MinHopsBetween(lvl, last, finish);
                checkpointsToFinishY = CrossedCount + Mathf.Max(0, dist);
            }
        }

        [Server]
        public void ServerResetCheckpoints()
        {
            if (crossedCheckpoints != null) crossedCheckpoints.Clear();
            checkpointsToFinishY = 0;
        }

        ArcadeCarController _car;
        Rigidbody _rb;
        Renderer[] _renderers;
        Dio.Powerups.States.CarStateMachine _stateMachine;

        bool _inCountdown;
        Vector3 _countdownStartPos;
        Quaternion _countdownStartRot;

        void Awake()
        {
            _car = GetComponent<ArcadeCarController>();
            _rb = GetComponent<Rigidbody>();
            _renderers = GetComponentsInChildren<Renderer>();
            _stateMachine = GetComponent<Dio.Powerups.States.CarStateMachine>();
            // Default off — DioCar.OnStartClient enables it for the owner.
            _car.readLocalInput = false;
            DioNetworkManager.OnRaceStarted += OnRaceStarted;
            DioNetworkManager.OnCountdownEnd += OnCountdownEnd;
        }

        void OnDestroy()
        {
            DioNetworkManager.OnRaceStarted -= OnRaceStarted;
            DioNetworkManager.OnCountdownEnd -= OnCountdownEnd;
        }

        void OnRaceStarted(bool isServer, RaceStartMessage msg)
        {
            if (isOwned)
            {
                _inCountdown = true;
                _countdownStartPos = transform.position;
                _countdownStartRot = transform.rotation;
            }
        }

        void OnCountdownEnd()
        {
            _inCountdown = false;
        }

        // Owner-only: apply powerup-state input modifications on top of the
        // local controller's read inputs. Runs BEFORE ArcadeCarController's
        // own FixedUpdate (DefaultExecutionOrder = -100) so its motor/steer
        // application sees the modified values. Skipped on the server (which
        // no longer simulates) and on remote clients (kinematic, no controller).
        void FixedUpdate()
        {
            if (!isOwned) return;
            if (_stateMachine == null) return;
            // Read raw inputs, let the state machine modify them, then write
            // back into currentInputs for ArcadeCarController.FixedUpdate to
            // consume this same tick.
            var raw = _car.ReadLocalInputsNow();
            _stateMachine.ModifyInputs(ref raw);
            _car.currentInputs = raw;
            // Tell the controller it doesn't need to re-read this tick.
            // (readLocalInput stays true so it falls back to ReadLocalInputsNow
            // if some other path skips this method.)

            // During countdown, reset velocity to lock position.
            if (_inCountdown)
            {
                _rb.velocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
        }

        void LateUpdate()
        {
            // During countdown, lock transform to start position.
            if (_inCountdown)
            {
                transform.SetPositionAndRotation(_countdownStartPos, _countdownStartRot);
            }
        }

        public override void OnStartServer()
        {
            // Cars are CLIENT-authoritative (NetworkTransform's syncDirection =
            // ClientToServer is set on the prefab). The server doesn't simulate
            // the car physics — it just receives transform updates from the
            // owner client and relays them to other clients. Mirror still
            // requires a Rigidbody on the server side for trigger callbacks +
            // collision queries against server-spawned obstacles, but it must
            // be kinematic so Unity physics doesn't fight the network writes.
            //
            // Host special case: OnStartClient runs after this and flips the
            // host's own car back to non-kinematic for full local sim.
            _rb.isKinematic = true;
            _rb.useGravity = false;
            _rb.interpolation = RigidbodyInterpolation.None;
            _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

            int connId = connectionToClient != null ? connectionToClient.connectionId : -1;
            Debug.Log($"[DioCar] OnStartServer netId={netId} ownerConn={connId} (kinematic; client-authoritative)");
        }

        public override void OnStartClient()
        {
            ApplyColor(colorIndex);
            ConfigureRigidbodyForRole();
            Debug.Log($"[DioCar] OnStartClient netId={netId} isServer={isServer} isOwned={isOwned} kinematic={_rb.isKinematic} readLocalInput={_car.readLocalInput}");
        }

        // Mirror calls this when this peer is given authority. For player
        // cars, this fires alongside OnStartClient on the owner — having both
        // is harmless and guarantees the kinematic/sim state is right even if
        // authority is reassigned later (host migration, etc).
        public override void OnStartAuthority() => ConfigureRigidbodyForRole();
        public override void OnStopAuthority() => ConfigureRigidbodyForRole();

        void ConfigureRigidbodyForRole()
        {
            // Owner runs full physics; everyone else is kinematic interpolation.
            // Host (isServer && isOwned) goes through this path too, ending at
            // full-sim — that's intentional, the host's car is owned by the
            // host's local client and they should feel zero input lag.
            bool fullSim = isOwned;

            if (fullSim)
            {
                _rb.isKinematic = false;
                _rb.useGravity = false; // SphericalGravity drives gravity
                _rb.interpolation = RigidbodyInterpolation.Interpolate;
                _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
            else
            {
                _rb.isKinematic = true;
                _rb.useGravity = false;
                _rb.interpolation = RigidbodyInterpolation.None;
                _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            }

            // Owner runs full physics, but inputs flow through DioCar.FixedUpdate
            // (which interleaves the powerup state machine before applying).
            // Setting readLocalInput=false on the controller prevents it from
            // overwriting `currentInputs` with the un-modified raw read.
            // Remote clients also keep readLocalInput=false (no inputs at all).
            _car.readLocalInput = false;
        }

        // ---- Color sync ----

        void OnColorChanged(int oldIdx, int newIdx) => ApplyColor(newIdx);

        void ApplyColor(int idx)
        {
            if (_renderers == null) return;
            var col = ColorPalette.Get(idx);
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                var goName = r.gameObject.name;
                if (goName != "Body" && goName != "Cabin") continue;

                // r.material auto-instantiates a per-renderer material at runtime
                // — that's the intended path here (not the edit-mode warning case).
                Color tint = goName == "Cabin" ? col * 0.7f : col;
                if (r.material.HasProperty("_ColorB")) r.material.SetColor("_ColorB", tint);
                else r.material.color = tint;
            }
        }

        [Server]
        public void ServerInitialize(DioPlayer owner)
        {
            ownerName = owner != null ? owner.playerName : "";
            colorIndex = owner != null ? owner.colorIndex : 0;
        }
    }
}
