using System.Collections.Generic;
using Mirror;
using UnityEngine;
using Dio.Player;

namespace Dio.Powerups.States
{
    /// Per-car state machine. Lives on the same GameObject as DioCar.
    /// Server simulates; clients receive a SyncList of currently-active state
    /// kinds for visual indicators (glow, shrunk-scale, etc.).
    [RequireComponent(typeof(DioCar))]
    public class CarStateMachine : NetworkBehaviour
    {
        readonly List<CarState> _states = new List<CarState>();
        readonly SyncList<int> _activeKinds = new SyncList<int>();

        DioCar _car;
        ArcadeCarController _controller;

        // Cached baseline so we can restore it.
        float _baseMaxSpeed;
        float _basePeakTorque;
        Vector3 _baseScale;

        public IReadOnlyList<CarState> Active => _states;

        public bool IsInvincible
        {
            get
            {
                foreach (var s in _states) if (s.Kind == PowerupKind.Star) return true;
                return false;
            }
        }

        void Awake()
        {
            _car = GetComponent<DioCar>();
            _controller = GetComponent<ArcadeCarController>();
            _baseMaxSpeed = _controller.maxSpeed;
            _basePeakTorque = _controller.peakMotorTorque;
            _baseScale = transform.localScale;
        }

        public override void OnStartClient() => _activeKinds.OnChange += OnKindsChanged;
        public override void OnStopClient() => _activeKinds.OnChange -= OnKindsChanged;

        [Server]
        public void Apply(CarState state)
        {
            if (state == null) return;
            state.Car = _car;
            state.OnEnter();
            _states.Add(state);
            _activeKinds.Add((int)state.Kind);
        }

        [Server]
        public void Clear(PowerupKind kind)
        {
            for (int i = _states.Count - 1; i >= 0; i--)
                if (_states[i].Kind == kind) RemoveAt(i);
        }

        void RemoveAt(int i)
        {
            var s = _states[i];
            s.OnExit();
            _states.RemoveAt(i);

            // Remove first matching kind from sync list.
            int idx = _activeKinds.IndexOf((int)s.Kind);
            if (idx >= 0) _activeKinds.RemoveAt(idx);
        }

        public void ModifyInputs(ref ArcadeCarController.Inputs inputs)
        {
            for (int i = 0; i < _states.Count; i++) _states[i].ModifyInputs(ref inputs);
        }

        void FixedUpdate()
        {
            if (!isServer) return;

            // Reset controller knobs to baseline before per-state mutations.
            _controller.maxSpeed = _baseMaxSpeed;
            _controller.peakMotorTorque = _basePeakTorque;

            for (int i = _states.Count - 1; i >= 0; i--)
            {
                var s = _states[i];
                s.OnTick(Time.fixedDeltaTime);
                if (s.IsExpired) { RemoveAt(i); continue; }
                s.ModifyController(_controller);
            }
        }

        // Visual sync — client-side scale change for Shrunk, etc.
        void OnKindsChanged(SyncList<int>.Operation op, int idx, int item)
        {
            ApplyVisualState();
        }

        void ApplyVisualState()
        {
            bool shrunk = false;
            foreach (var k in _activeKinds)
            {
                if (k == (int)PowerupKind.Lightning) shrunk = true;
                // Add more visual hooks here (e.g. star glow particles).
            }
            transform.localScale = shrunk ? _baseScale * 0.55f : _baseScale;
        }
    }
}
