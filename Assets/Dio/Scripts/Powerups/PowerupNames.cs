namespace Dio.Powerups
{
    /// Display strings for each PowerupKind. Used by the HUD pickup banner
    /// and (later) any UI that needs to name the powerup.
    public static class PowerupNames
    {
        public static string Display(PowerupKind kind)
        {
            switch (kind)
            {
                case PowerupKind.Boost:       return "BOOST";
                case PowerupKind.TripleBoost: return "TRIPLE BOOST";
                case PowerupKind.Star:        return "STAR";
                case PowerupKind.Lightning:   return "LIGHTNING";
                case PowerupKind.Banana:      return "BANANA";
                case PowerupKind.OilSlick:    return "OIL SLICK";
                case PowerupKind.GreenShell:  return "GREEN SHELL";
                case PowerupKind.BlueShell:   return "BLUE SHELL";
                case PowerupKind.Bobomb:      return "BOB-OMB";
                case PowerupKind.Tornado:     return "TORNADO";
                case PowerupKind.RedShell:    return "RED SHELL";
                case PowerupKind.FreezeRay:   return "FREEZE RAY";
                case PowerupKind.Confusion:   return "CONFUSION";
                case PowerupKind.Magnet:      return "MAGNET";
                case PowerupKind.SpringTrap:  return "SPRING";
                default:                      return "";
            }
        }
    }
}
