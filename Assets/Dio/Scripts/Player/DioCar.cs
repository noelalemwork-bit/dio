using Mirror;
using UnityEngine;
using Dio.Common;
using Dio.Net;

namespace Dio.Player
{
    /// Networked car. Input flows: local player → Cmd → server → physics
    /// → NetworkTransform → all clients (visual interpolation).
    ///
    /// Authority: server-only. Local player never moves the rigidbody directly
    /// in the M1 milestone; predicted-rigidbody is the M3 upgrade path
    /// (see plan.md §4.3 Phase B).
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(ArcadeCarController))]
    [RequireComponent(typeof(SphericalGravity))]
    [DefaultExecutionOrder(-100)] // before ArcadeCarController.FixedUpdate
    public class DioCar : NetworkBehaviour
    {
        [SyncVar(hook = nameof(OnColorChanged))] public int colorIndex;
        [SyncVar] public string ownerName = "";

        [Tooltip("How often the local player sends its inputs to the server (Hz).")]
        public float inputSendRate = 30f;

        ArcadeCarController _car;
        Rigidbody _rb;
        Renderer[] _renderers;
        Dio.Powerups.States.CarStateMachine _stateMachine;
        float _nextSendTime;
        bool _loggedFirstInputSend;
        bool _loggedFirstInputRecv;

        // Latest received inputs (server-side).
        ArcadeCarController.Inputs _latestInputs;

        void Awake()
        {
            _car = GetComponent<ArcadeCarController>();
            _rb = GetComponent<Rigidbody>();
            _renderers = GetComponentsInChildren<Renderer>();
            _stateMachine = GetComponent<Dio.Powerups.States.CarStateMachine>();
            // Network drives inputs; the controller never reads input itself.
            _car.readLocalInput = false;
        }

        public override void OnStartServer()
        {
            // Server owns the rigidbody simulation.
            _rb.isKinematic = false;
            int connId = connectionToClient != null ? connectionToClient.connectionId : -1;
            Debug.Log($"[DioCar] OnStartServer netId={netId} ownerConn={connId} (server simulates physics)");
        }

        public override void OnStartClient()
        {
            ApplyColor(colorIndex);
            // On clients, the rigidbody is driven by NetworkTransform; the
            // server's authoritative position is interpolated to us. Keep it
            // kinematic on remote clients so its physics doesn't double-step.
            if (!isServer) _rb.isKinematic = true;
            Debug.Log($"[DioCar] OnStartClient netId={netId} isServer={isServer} isOwned={isOwned} kinematic={_rb.isKinematic}");
        }

        void Update()
        {
            // The car is OWNED by the connection but it's not the connection's
            // player object (DioPlayer is). So `isOwned`, not `isLocalPlayer`.
            if (!isOwned) return;

            // Throttle input upload to the server.
            if (Time.unscaledTime < _nextSendTime) return;
            _nextSendTime = Time.unscaledTime + 1f / Mathf.Max(1f, inputSendRate);

            var inputs = _car.ReadLocalInputsNow();

            if (!_loggedFirstInputSend)
            {
                _loggedFirstInputSend = true;
                Debug.Log($"[DioCar] netId={netId} first input send: steer/throttle={inputs.steerThrottle} brake={inputs.brake} hb={inputs.handbrake}");
            }

            CmdSendInputs(inputs.steerThrottle, inputs.brake, inputs.handbrake);
        }

        [Command]
        void CmdSendInputs(Vector2 steerThrottle, bool brake, bool handbrake)
        {
            // Trivial sanity clamp; this is the only place untrusted client data enters server simulation.
            _latestInputs = new ArcadeCarController.Inputs
            {
                steerThrottle = new Vector2(Mathf.Clamp(steerThrottle.x, -1f, 1f),
                                            Mathf.Clamp(steerThrottle.y, -1f, 1f)),
                brake = brake,
                handbrake = handbrake,
            };

            if (!_loggedFirstInputRecv)
            {
                _loggedFirstInputRecv = true;
                int connId = connectionToClient != null ? connectionToClient.connectionId : -1;
                Debug.Log($"[DioCar] SERVER netId={netId} got first input from conn={connId}: steer/throttle={steerThrottle}");
            }
        }

        void FixedUpdate()
        {
            if (!isServer) return;
            var inputs = _latestInputs;
            if (_stateMachine != null) _stateMachine.ModifyInputs(ref inputs);
            _car.currentInputs = inputs;
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
