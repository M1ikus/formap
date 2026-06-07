using System;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// Bounding box w 2D — analog do formap.BBox ale w shared library bez Unity dependency.
    /// Używany dla AdminRegion / quick-reject w PIP tests / spatial culling.
    /// </summary>
    [Serializable]
    public struct GraphBBox
    {
        public float MinX;
        public float MinY;
        public float MaxX;
        public float MaxY;

        public static readonly GraphBBox Empty = new GraphBBox
        {
            MinX = float.MaxValue,
            MinY = float.MaxValue,
            MaxX = float.MinValue,
            MaxY = float.MinValue
        };

        public float Width => MaxX - MinX;
        public float Height => MaxY - MinY;

        public bool Contains(float x, float y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
        public bool Contains(GraphPoint p) => Contains(p.X, p.Y);

        public void Expand(float x, float y)
        {
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
        }

        public void Expand(GraphPoint p) => Expand(p.X, p.Y);

        public bool Overlaps(GraphBBox other)
            => !(other.MaxX < MinX || other.MinX > MaxX || other.MaxY < MinY || other.MinY > MaxY);

        public override string ToString() => $"[{MinX:F2},{MinY:F2}] to [{MaxX:F2},{MaxY:F2}]";
    }
}
