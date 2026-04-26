using System.Collections.Generic;
using Mirror;
using UnityEngine;
using Dio.Player;

namespace Dio.Powerups.States
{
    /// Per-car state machine. Lives on the same GameObject as DioCar.
    ///
    /// Server authority on the *timeline*: the server creates/removes states
    /// and ticks their durations. The active set is broadcast as a SyncList of
    /// PowerupKind ints so every peer knows what's currently on each car.
    ///
    /// Owner-side application: with client-authority physics (DioCar refactor),
    /// the OWNER is the only peer running the controller — so the owner must
    /// apply ModifyController + ModifyInputs locally. The factory rebuilds
    /// short-lived "client copy" CarState instances from the synced kinds; we
    /// don't sync per-state internals (Slip seed, Star elapsed, etc.) — the
    /// effects are stable enough across peers without that fidelity.
    [RequireComponent(typeof(DioCar))]
    [DefaultExecutionOrder(-50)] // after DioCar (-100), before ArcadeCarController (0)
    public class CarStateMachine : NetworkBehaviour
    {
        readonly List<CarState> _serverStates = new List<CarState>();
        readonly List<CarState> _clientStates = new List<CarState>(); // owner mirror
        readonly SyncList<int> _activeKinds = new SyncList<int>();

        DioCar _car;
        ArcadeCarController _controller;

        // Cached baseline so we can restore it.
        float _baseMaxSpeed;
        float _basePeakTorque;
        Vector3 _baseScale;

        public IReadOnlyList<CarState> Active => _serverStates;
        public IReadOnlyList<int> ActiveKinds => _activeKinds;

        public bool IsInvincible
        {
            get
            {
                foreach (var s in _serverStates) if (s.Kind == PowerupKind.Star) return true;
                foreach (var k in _activeKinds) if ((PowerupKind)k == PowerupKind.Star) return true;
                return false;
            }
        }

        public bool IsActive(PowerupKind kind)
        {
            foreach (var k in _activeKinds) if ((PowerupKind)k == kind) return true;
            return false;
        }

        void Awake()
        {
            _car = GetComponent<DioCar>();
            _controller = GetComponent<ArcadeCarController>();
            _baseMaxSpeed = _controller.maxSpeed;
            _basePeakTorque = _controller.peakMotorTorque;
            _baseScale = transform.localScale;
        }

        public override void OnStartClient()
        {
            _activeKinds.OnChange += OnKindsChanged;
            // Catch up on any kinds already in the list at spawn time.
            RebuildClientStates();
            ApplyVisualState();
        }

        public override void OnStopClient()
        {
            _activeKinds.OnChange -= OnKindsChanged;
            for (int i = 0; i < _clientStates.Count; i++) _clientStates[i].OnExit();
            _clientStates.Clear();
        }

        [Server]
        public void Apply(CarState state)
        {
            if (state == null) return;
            state.Car = _car;
            state.OnEnter();
            _serverStates.Add(state);
            _activeKinds.Add((int)state.Kind);
        }

        [Server]
        public void Clear(PowerupKind kind)
        {
            for (int i = _serverStates.Count - 1; i >= 0; i--)
                if (_serverStates[i].Kind == kind) RemoveAt(i);
        }

        void RemoveAt(int i)
        {
            var s = _serverStates[i];
            s.OnExit();
            _serverStates.RemoveAt(i);

            // Remove first matching kind from sync list.
            int idx = _activeKinds.IndexOf((int)s.Kind);
            if (idx >= 0) _activeKinds.RemoveAt(idx);
        }

        // Called by DioCar.FixedUpdate on the owner. The server has the
        // authoritative copy; the owner uses its local mirror to keep the
        // input-modify pass working when the server isn't simulating.
        public void ModifyInputs(ref ArcadeCarController.Inputs inputs)
        {
            // Use server's instances if available; otherwise the client mirror.
            var src = _serverStates.Count > 0 ? _serverStates : _clientStates;
            for (int i = 0; i < src.Count; i++) src[i].ModifyInputs(ref inputs);
        }

        void FixedUpdate()
        {
            // Server: tick durations + remove expired (drives the SyncList).
            if (isServer)
            {
                for (int i = _serverStates.Count - 1; i >= 0; i--)
                {
                    var s = _serverStates[i];
                    s.OnTick(Time.fixedDeltaTime);
                    if (s.IsExpired) { RemoveAt(i); continue; }
                }
            }

            // Owner: apply ModifyController to its local controller every tick.
            // (Server, when it's not the host, has a kinematic controller that
            // doesn't simulate, so writes are wasted there.) Reset baseline
            // first so multiple boosts compose deterministically and old
            // boosts don't leave the controller permanently bumped.
            if (_car != null && _car.isOwned)
            {
                _controller.maxSpeed = _baseMaxSpeed;
                _controller.peakMotorTorque = _basePeakTorque;

                // Prefer server's authoritative state instances when we are
                // host; fall back to the client mirror otherwise.
                var src = isServer ? _serverStates : _clientStates;
                for (int i = 0; i < src.Count; i++) src[i].ModifyController(_controller);
            }
        }

        // SyncList callback. Rebuild the client mirror + refresh visuals.
        void OnKindsChanged(SyncList<int>.Operation op, int idx, int item)
        {
            RebuildClientStates();
            ApplyVisualState();
        }

        void RebuildClientStates()
        {
            var next = new List<CarState>(_activeKinds.Count);
            foreach (var k in _activeKinds)
            {
                var kind = (PowerupKind)k;
                CarState existing = null;
                for (int i = 0; i < _clientStates.Count; i++)
                {
                    if (_clientStates[i].Kind != kind) continue;
                    existing = _clientStates[i];
                    _clientStates.RemoveAt(i);
                    break;
                }

                if (existing == null)
                {
                    existing = CarStateFactory.Create(kind);
                    if (existing != null)
                    {
                        existing.Car = _car;
                        if (!isServer) existing.OnEnter();
                    }
                }

                if (existing != null)
                    next.Add(existing);
            }

            for (int i = 0; i < _clientStates.Count; i++)
                if (!isServer) _clientStates[i].OnExit();

            _clientStates.Clear();
            _clientStates.AddRange(next);
        }

        void ApplyVisualState()
        {
            transform.localScale = _baseScale;
        }
    }
}
