using System.Collections;
using Mirror;
using TMPro;
using UnityEngine;
using Dio.CameraRig;
using Dio.Net;
using Dio.Player;

namespace Dio.UI
{
    /// Cinematic race-start sequence: swoops the camera down from above the
    /// player's head, runs a "3 ... 2 ... 1 ... GO!" countdown with bouncy
    /// pop+fade animation, and locks every local car's inputs until the GO!
    /// frame so the start lines up exactly with the race-active gate.
    ///
    /// Lives on the RaceHUD root so it has access to the countdown TMP text.
    /// All clients subscribe to `DioNetworkManager.OnRaceStarted`; the
    /// `startServerTime` field on the race-start message is the shared GO!
    /// instanupt that every peer ticks toward via `NetworkTime.time`.
    public class RaceIntro : MonoBehaviour
    {
        [Header("Countdown text")]
        public CanvasGroup countdownGroup;
        public TMP_Text countdownLabel;

        [Header("Cinematic swoop")]
        [Tooltip("Distance above the local player at which the camera starts (along surface-up).")]
        public float swoopStartAltitude = 90f;
        [Tooltip("Lerp factor while swooping in. Lower = slower, more dramatic.")]
        public float swoopChaseLerp = 1.2f;
        [Tooltip("Lerp factor restored when GO! fires.")]
        public float runChaseLerp = 8f;

        [Header("Countdown timing")]
        [Tooltip("Seconds before GO! that the '3' tick fires. Should be < race-start delay.")]
        public float countdownLeadIn = 3f;
        [Tooltip("Seconds the GO! text stays on screen after race start.")]
        public float goHoldSeconds = 0.6f;

        Coroutine _running;

        void Awake()
        {
            DioNetworkManager.OnRaceStarted += OnRaceStarted;
            DioNetworkManager.OnRaceWon += OnRaceWon;
            HideCountdown();
        }

        void OnDestroy()
        {
            DioNetworkManager.OnRaceStarted -= OnRaceStarted;
            DioNetworkManager.OnRaceWon -= OnRaceWon;
        }

        void OnRaceStarted(bool _, RaceStartMessage msg)
        {
            if (_running != null) StopCoroutine(_running);
            _running = StartCoroutine(IntroSequence(msg.startServerTime));
        }

        // Stop the countdown if the race is forcibly ended (e.g. while the
        // intro is still running, which shouldn't happen but guards against
        // edge cases in the cleanup flow).
        void OnRaceWon(RaceWonMessage _)
        {
            if (_running != null) { StopCoroutine(_running); _running = null; }
            HideCountdown();
        }

        IEnumerator IntroSequence(double goTime)
        {
            // Lock every car (the local owner's authoritative controller will
            // ignore inputs while this flag is on; remote cars don't tick
            // their controller, so the flag is effectively decorative there).
            SetAllCarInputsLocked(true);

            // Push the chase camera up high so it starts above and visibly
            // swoops down. The RaceCamera component is added by RaceBootstrap
            // *after* the local owned car is found, which can be a frame or
            // two after race-start fires — keep retrying until we succeed
            // (or until the countdown is about to begin).
            RaceCamera rc = null;
            double tickStart = goTime - countdownLeadIn;
            while (rc == null && NetworkTime.time < tickStart)
            {
                rc = SetupSwoopCamera();
                yield return null;
                // Re-lock every frame so cars spawned mid-intro start locked too.
                SetAllCarInputsLocked(true);
            }

            // Continue locking inputs through the countdown.
            while (NetworkTime.time < tickStart) { SetAllCarInputsLocked(true); yield return null; }

            yield return TickPop("3");
            yield return WaitUntilNetworkTime(goTime - 2.0);
            yield return TickPop("2");
            yield return WaitUntilNetworkTime(goTime - 1.0);
            yield return TickPop("1");
            yield return WaitUntilNetworkTime(goTime);

            // GO! — restore camera lerp + unlock cars + hold the text.
            if (rc != null) rc.chasePosLerp = runChaseLerp;
            SetAllCarInputsLocked(false);
            DioNetworkManager.OnCountdownEnd?.Invoke();
            yield return TickPop("GO!", durationSeconds: goHoldSeconds, isGo: true);

            HideCountdown();
            _running = null;
        }

        IEnumerator WaitUntilNetworkTime(double t)
        {
            while (NetworkTime.time < t) yield return null;
        }

        // Single bouncy pop animation: the text scales up from 0.4× to 1.2×
        // with an overshoot ease, holds at 1×, then fades. GO! holds longer.
        IEnumerator TickPop(string text, float durationSeconds = 0.95f, bool isGo = false)
        {
            if (countdownLabel == null || countdownGroup == null) yield break;
            countdownLabel.text = text;
            countdownLabel.color = isGo
                ? new Color(0.40f, 0.95f, 0.45f, 1f)  // green for GO!
                : Color.Lerp(new Color(0.95f, 0.65f, 0.20f, 1f),
                             new Color(0.95f, 0.32f, 0.28f, 1f),
                             Mathf.Clamp01(int.TryParse(text, out var n) ? (3 - n) / 2f : 0f));
            countdownGroup.alpha = 1f;

            float t = 0f;
            var tr = countdownLabel.rectTransform;
            while (t < durationSeconds)
            {
                t += Time.unscaledDeltaTime;
                float u = t / durationSeconds;
                // Bouncy overshoot: pop quickly to 1.2×, settle to 1.0× by 50%.
                float scale;
                if (u < 0.18f)
                    scale = Mathf.Lerp(0.4f, 1.25f, u / 0.18f);
                else if (u < 0.45f)
                    scale = Mathf.Lerp(1.25f, 0.95f, (u - 0.18f) / 0.27f);
                else if (u < 0.65f)
                    scale = Mathf.Lerp(0.95f, 1.05f, (u - 0.45f) / 0.20f);
                else
                    scale = 1.0f;
                tr.localScale = Vector3.one * scale;

                // Hold full alpha until 70%, then fade.
                countdownGroup.alpha = u < 0.70f ? 1f : Mathf.Lerp(1f, 0f, (u - 0.70f) / 0.30f);
                yield return null;
            }
            countdownGroup.alpha = 0f;
        }

        void HideCountdown()
        {
            if (countdownGroup != null) countdownGroup.alpha = 0f;
            if (countdownLabel != null) countdownLabel.text = "";
        }

        RaceCamera SetupSwoopCamera()
        {
            var cam = Camera.main;
            if (cam == null) return null;
            var rc = cam.GetComponent<RaceCamera>();
            if (rc == null) return null;
            rc.chasePosLerp = swoopChaseLerp;

            if (rc.target != null && rc.gravity != null)
            {
                Vector3 up = rc.gravity.PlanetUp;
                Vector3 fwd = Vector3.ProjectOnPlane(rc.target.forward, up).normalized;
                if (fwd.sqrMagnitude < 1e-4f) fwd = rc.target.forward;
                Vector3 startPos = rc.target.position + up * swoopStartAltitude - fwd * 1.0f;
                Quaternion startRot = Quaternion.LookRotation((rc.target.position - startPos).normalized, fwd);
                cam.transform.SetPositionAndRotation(startPos, startRot);
            }
            return rc;
        }

        static void SetAllCarInputsLocked(bool locked)
        {
            // Iterate spawned client objects — host's local copies and joined
            // clients share the same NetworkClient.spawned dictionary.
            foreach (var ni in NetworkClient.spawned.Values)
            {
                var ac = ni != null ? ni.GetComponent<ArcadeCarController>() : null;
                if (ac != null) ac.inputsLocked = locked;
            }
        }
    }
}
