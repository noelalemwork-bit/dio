namespace Dio.Powerups
{
    /// All powerups, including the ones we've only stubbed the design for.
    /// Implemented = has a PowerupEffect entry in PowerupRegistry.
    public enum PowerupKind : byte
    {
        None            = 0,

        // --- Self-buffs ---
        Boost           = 1,    // implemented
        TripleBoost     = 2,    // implemented (3 stacked single boosts)
        Star            = 3,    // implemented (invincible + speed)

        // --- Global effects ---
        Lightning       = 10,   // implemented (shrinks + slows others)

        // --- Static ground hazards ---
        Banana          = 20,   // implemented
        OilSlick        = 21,   // implemented

        // --- Forward projectiles ---
        GreenShell      = 30,   // implemented
        BlueShell       = 31,   // implemented (targets leader)
        Bobomb          = 32,   // implemented

        // --- Wandering obstacles ---
        Tornado         = 40,   // implemented

        // --- Designed but not yet implemented (M2+) ---
        RedShell        = 50,   // homing projectile
        FreezeRay       = 51,   // beam: target frozen state
        Confusion       = 52,   // target's steering inverted
        Magnet          = 53,   // radial pull on nearby cars
        SpringTrap      = 54,   // bounce impulse on contact
    }
}
