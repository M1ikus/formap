namespace RailwayManager.GraphData
{
    /// <summary>
    /// Fallback Vmax dla linii kolejowej gdy tag maxspeed jest nieobecny lub nieprawidłowy.
    /// Decyzja na podstawie OSM tagów usage/service. Port z Unity LineUsageSpeedCatalog
    /// do shared library — używana w pre-build edges.
    /// </summary>
    public static class GraphLineUsageSpeedCatalog
    {
        public const int Main       = 140;
        public const int Branch     = 120;
        public const int Secondary  = 100;
        public const int Industrial = 60;
        public const int Siding     = 40;
        public const int Yard       = 30;
        public const int Spur       = 40;
        public const int Crossover  = 40;
        public const int Unknown    = 80;

        public static int GetFallbackSpeed(string usageTag, string serviceTag)
        {
            if (!string.IsNullOrEmpty(serviceTag))
            {
                switch (serviceTag.ToLowerInvariant())
                {
                    case "siding":    return Siding;
                    case "yard":      return Yard;
                    case "spur":      return Spur;
                    case "crossover": return Crossover;
                }
            }

            if (!string.IsNullOrEmpty(usageTag))
            {
                switch (usageTag.ToLowerInvariant())
                {
                    case "main":        return Main;
                    case "branch":      return Branch;
                    case "industrial":
                    case "tourism":
                    case "military":    return Industrial;
                }
            }

            return Unknown;
        }

        public static int ParseMaxSpeed(string rawMaxSpeed)
        {
            if (string.IsNullOrWhiteSpace(rawMaxSpeed)) return 0;
            var s = rawMaxSpeed.Trim().ToLowerInvariant();
            if (s == "none" || s == "signals" || s == "variable") return 0;
            int semi = s.IndexOf(';');
            if (semi > 0) s = s.Substring(0, semi);
            s = s.Replace("km/h", "").Replace("kmh", "").Trim();
            if (s.Contains(":")) return 0;
            if (int.TryParse(s, out var value) && value > 0 && value <= 350)
                return value;
            return 0;
        }
    }
}
