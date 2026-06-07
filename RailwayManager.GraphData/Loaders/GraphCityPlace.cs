namespace RailwayManager.GraphData
{
    public enum GraphPlaceType
    {
        City,    // place=city — large cities
        Town,    // place=town — medium-sized
        Village  // place=village — villages
    }

    /// <summary>
    /// City/town/village from OSM. Ported from Unity's Timetable.CityPlace.
    /// </summary>
    public class GraphCityPlace
    {
        public string? Name;
        public GraphPoint Position;
        public GraphPlaceType Type;
        public int Population;        // 0 if the tag is missing
        public string? Voivodeship;    // filled in by the resolver post-build

        public bool IsMajor => Type == GraphPlaceType.City || Type == GraphPlaceType.Town;
    }
}
