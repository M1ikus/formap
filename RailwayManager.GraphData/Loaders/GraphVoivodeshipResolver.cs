using System.Collections.Generic;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Classifies a position → voivodeship based on AdminRegions.
    /// Ported from Unity's Timetable.VoivodeshipResolver.
    /// </summary>
    public class GraphVoivodeshipResolver
    {
        private readonly List<GraphAdminRegion> _voivodeships;
        private readonly List<GraphAdminRegion> _countries;

        public int VoivodeshipCount => _voivodeships.Count;
        public int CountryCount => _countries.Count;

        public GraphVoivodeshipResolver(List<GraphAdminRegion> regions)
        {
            _voivodeships = new List<GraphAdminRegion>();
            _countries = new List<GraphAdminRegion>();
            if (regions == null) return;

            foreach (var r in regions)
            {
                if (r.AdminLevel == 4) _voivodeships.Add(r);
                else if (r.AdminLevel == 2) _countries.Add(r);
            }
        }

        public string? GetVoivodeship(GraphPoint pos)
        {
            foreach (var v in _voivodeships)
                if (v.ContainsPoint(pos)) return v.Name;
            return null;
        }

        public string? GetCountry(GraphPoint pos)
        {
            foreach (var c in _countries)
                if (c.ContainsPoint(pos)) return c.Name;
            return null;
        }

        public bool IsReady => _voivodeships.Count > 0;
    }
}
