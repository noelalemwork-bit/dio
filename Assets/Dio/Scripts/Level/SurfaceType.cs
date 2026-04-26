using UnityEngine;

namespace Dio.Level
{
    /// What the track's made of at this point. Drives wheel friction, drag,
    /// brake response, and the ribbon's tinted material — Trackmania-style
    /// "drive over a different colour, the car drives differently".
    public enum SurfaceType
    {
        /// Default. Standard friction, standard grip, standard brakes.
        Asphalt = 0,
        /// Slick. Halved sideways grip, reduced longitudinal friction. The
        /// car slides through corners.
        Ice = 1,
        /// Loose. Reduced grip both axes; brakes still work but driving feels
        /// "floaty" / drifty.
        Dirt = 2,
        /// High drag. Ground robs forward speed but grip stays normal —
        /// great for slowing a car you can't catch otherwise.
        Grass = 3,
        /// Sticky. Boosts longitudinal friction (no slide on hard turns) and
        /// dampens lateral kicks. Used for "magnet road" sections.
        Magnetic = 4,
        /// Bouncy. Apply a small impulse along the surface normal whenever
        /// a wheel touches — used for trampoline / boost-pad strips.
        Boost = 5,
    }

    /// Per-surface physics modifiers applied to the wheel that's in contact.
    /// All multipliers are applied on top of the controller's own values; 1.0
    /// = no change. Consumers should clamp the result if they're worried
    /// about extreme combinations (bug-bait until balance is locked).
    [System.Serializable]
    public struct SurfaceProperties
    {
        public float forwardFrictionMultiplier;
        public float sidewaysFrictionMultiplier;
        public float dragMultiplier;
        public float brakeMultiplier;
        public float launchImpulse;          // along surface normal, used by Boost
        public Color tintColor;              // applied to the material's base colour
    }

    public static class SurfacePropertyTable
    {
        public static SurfaceProperties Get(SurfaceType type)
        {
            switch (type)
            {
                case SurfaceType.Ice:
                    return new SurfaceProperties
                    {
                        forwardFrictionMultiplier = 0.55f,
                        sidewaysFrictionMultiplier = 0.30f,
                        dragMultiplier = 0.6f,
                        brakeMultiplier = 0.45f,
                        launchImpulse = 0f,
                        tintColor = new Color(0.74f, 0.92f, 0.98f, 1f),
                    };
                case SurfaceType.Dirt:
                    return new SurfaceProperties
                    {
                        forwardFrictionMultiplier = 0.80f,
                        sidewaysFrictionMultiplier = 0.55f,
                        dragMultiplier = 1.10f,
                        brakeMultiplier = 0.85f,
                        launchImpulse = 0f,
                        tintColor = new Color(0.50f, 0.36f, 0.20f, 1f),
                    };
                case SurfaceType.Grass:
                    return new SurfaceProperties
                    {
                        forwardFrictionMultiplier = 0.75f,
                        sidewaysFrictionMultiplier = 0.85f,
                        dragMultiplier = 1.45f,
                        brakeMultiplier = 1.10f,
                        launchImpulse = 0f,
                        tintColor = new Color(0.34f, 0.64f, 0.30f, 1f),
                    };
                case SurfaceType.Magnetic:
                    return new SurfaceProperties
                    {
                        forwardFrictionMultiplier = 1.20f,
                        sidewaysFrictionMultiplier = 1.45f,
                        dragMultiplier = 0.95f,
                        brakeMultiplier = 1.20f,
                        launchImpulse = 0f,
                        tintColor = new Color(0.78f, 0.40f, 1.0f, 1f),
                    };
                case SurfaceType.Boost:
                    return new SurfaceProperties
                    {
                        forwardFrictionMultiplier = 1.0f,
                        sidewaysFrictionMultiplier = 1.0f,
                        dragMultiplier = 0.85f,
                        brakeMultiplier = 1.0f,
                        launchImpulse = 18f,
                        tintColor = new Color(1.0f, 0.62f, 0.20f, 1f),
                    };
                case SurfaceType.Asphalt:
                default:
                    return new SurfaceProperties
                    {
                        forwardFrictionMultiplier = 1.0f,
                        sidewaysFrictionMultiplier = 1.0f,
                        dragMultiplier = 1.0f,
                        brakeMultiplier = 1.0f,
                        launchImpulse = 0f,
                        tintColor = new Color(0.10f, 0.10f, 0.12f, 1f),
                    };
            }
        }
    }

    /// Tag component placed on track / spawn-pad / hazard meshes. Wheels
    /// query the contact collider's GetComponent<SurfaceComponent> to find
    /// out which physics profile to apply. Missing component = Asphalt.
    public class SurfaceComponent : MonoBehaviour
    {
        public SurfaceType type = SurfaceType.Asphalt;
    }
}
