using UnityEngine;
using Dio.Player;

namespace Dio.Powerups
{
    /// Server-side execution unit for a powerup. The PowerupHolder grabs the
    /// effect for the held kind and calls Activate(owner). Effects either:
    ///   * apply a CarState to the owner (self-buff)
    ///   * apply a CarState globally (lightning)
    ///   * spawn a networked obstacle / projectile (shells, bombs, tornado)
    public abstract class PowerupEffect
    {
        public abstract PowerupKind Kind { get; }

        /// Server-only entry point. Called by PowerupHolder.CmdActivate.
        public abstract void Activate(DioCar owner);
    }
}
