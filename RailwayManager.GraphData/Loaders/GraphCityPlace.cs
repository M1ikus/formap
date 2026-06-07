namespace RailwayManager.GraphData
{
    public enum GraphPlaceType
    {
        City,    // place=city — duże miasta
        Town,    // place=town — średnie
        Village  // place=village — wsie
    }

    /// <summary>
    /// Miasto/miejscowość/wieś z OSM. Port z Unity Timetable.CityPlace.
    /// </summary>
    public class GraphCityPlace
    {
        public string? Name;
        public GraphPoint Position;
        public GraphPlaceType Type;
        public int Population;        // 0 jeśli brak tagu
        public string? Voivodeship;    // wypełniany przez resolver post-build

        public bool IsMajor => Type == GraphPlaceType.City || Type == GraphPlaceType.Town;
    }
}
