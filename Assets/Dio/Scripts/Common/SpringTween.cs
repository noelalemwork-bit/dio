using System;
using UnityEngine;

namespace Dio.Common
{
    /// Drop-on UI animator. Implements a 1D damped spring (per channel) for
    /// values that should *feel* alive — buttons growing on hover, swatches
    /// popping on select, panels easing into place. Bouncier than a Lerp,
    /// which is the brief: "no just lerped, cubic and springy whenever
    /// possible, bouncy".
    ///
    /// Use SpringTween.Animate to drive Vector3 localScale, anchoredPosition,
    /// or any other vector property. The math is a critically-tunable damped
    /// harmonic oscillator (Erin Catto's stable form, frame-rate independent
    /// even at large dt).
    public static class SpringTween
    {
        /// Step a 1D spring toward `target` from `current` with `velocity`.
        /// `omega` = angular frequency (2π / period), `zeta` = damping
        /// ratio (0 = bounces forever, 1 = critically damped, > 1 =
        /// over-damped, < 1 = underdamped springy bounce).
        public static void Step1D(ref float current, ref float velocity,
            float target, float dt, float omega, float zeta)
        {
            // Implicit-Euler closed-form solution. Stable for arbitrary dt.
            float f = 1f + 2f * dt * zeta * omega;
            float oo = omega * omega;
            float hoo = dt * oo;
            float hhoo = dt * hoo;
            float det = f + hhoo;
            float detPos = (f * current + dt * velocity + hhoo * target) / det;
            float detVel = (velocity + hoo * (target - current)) / det;
            current = detPos;
            velocity = detVel;
        }

        public static void Step3D(ref Vector3 current, ref Vector3 velocity,
            Vector3 target, float dt, float omega, float zeta)
        {
            Step1D(ref current.x, ref velocity.x, target.x, dt, omega, zeta);
            Step1D(ref current.y, ref velocity.y, target.y, dt, omega, zeta);
            Step1D(ref current.z, ref velocity.z, target.z, dt, omega, zeta);
        }

        public static void Step2D(ref Vector2 current, ref Vector2 velocity,
            Vector2 target, float dt, float omega, float zeta)
        {
            Step1D(ref current.x, ref velocity.x, target.x, dt, omega, zeta);
            Step1D(ref current.y, ref velocity.y, target.y, dt, omega, zeta);
        }

        public static float Omega(float period) => 2f * Mathf.PI / Mathf.Max(0.001f, period);
    }

    /// Spring-tween a RectTransform's localScale toward a target. Driven by
    /// the simple fact that lots of UI states (selected vs. not, hovered vs.
    /// not) read better as a scale pulse than a color flash.
    [DisallowMultipleComponent]
    public class SpringScale : MonoBehaviour
    {
        public Vector3 target = Vector3.one;
        public float period = DioStyle.SpringPeriod;
        public float damping = DioStyle.SpringDamping;

        Vector3 _current = Vector3.one;
        Vector3 _velocity;
        bool _initialized;

        public void SetTargetScale(float uniform) { target = Vector3.one * uniform; enabled = true; }
        public void SetTargetScale(Vector3 v)     { target = v; enabled = true; }
        public void Snap(Vector3 v) { target = v; _current = v; _velocity = Vector3.zero; transform.localScale = v; enabled = false; }

        void OnEnable()
        {
            if (!_initialized)
            {
                _current = transform.localScale;
                _initialized = true;
            }
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float omega = SpringTween.Omega(period);
            SpringTween.Step3D(ref _current, ref _velocity, target, dt, omega, damping);
            transform.localScale = _current;

            // Settle: stop driving Update once we're visually static.
            if ((_current - target).sqrMagnitude < 1e-6f && _velocity.sqrMagnitude < 1e-4f)
            {
                _current = target;
                transform.localScale = target;
                enabled = false;
            }
        }
    }

    /// Spring-tween a RectTransform's anchored position toward a target.
    [DisallowMultipleComponent]
    public class SpringAnchoredPos : MonoBehaviour
    {
        public Vector2 target;
        public float period = DioStyle.SpringPeriod;
        public float damping = DioStyle.SpringDamping;

        Vector2 _current;
        Vector2 _velocity;
        RectTransform _rt;
        bool _initialized;

        public void SetTarget(Vector2 v) { target = v; enabled = true; }
        public void Snap(Vector2 v)
        {
            target = v; _current = v; _velocity = Vector2.zero;
            if (_rt == null) _rt = transform as RectTransform;
            if (_rt != null) _rt.anchoredPosition = v;
            enabled = false;
        }

        void OnEnable()
        {
            _rt = transform as RectTransform;
            if (!_initialized && _rt != null)
            {
                _current = _rt.anchoredPosition;
                _initialized = true;
            }
        }

        void Update()
        {
            if (_rt == null) return;
            float dt = Time.unscaledDeltaTime;
            float omega = SpringTween.Omega(period);
            SpringTween.Step2D(ref _current, ref _velocity, target, dt, omega, damping);
            _rt.anchoredPosition = _current;

            if ((_current - target).sqrMagnitude < 0.01f && _velocity.sqrMagnitude < 0.05f)
            {
                _current = target;
                _rt.anchoredPosition = target;
                enabled = false;
            }
        }
    }
}
