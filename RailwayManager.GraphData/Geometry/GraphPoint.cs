using System;

namespace RailwayManager.GraphData
{
    /// <summary>
    /// 2D point z X/Y floats — blittable, używany jako Vector2 equivalent w shared library.
    /// Eliminuje dependency na UnityEngine.Vector2 (który jest tylko w Unity) lub
    /// System.Numerics.Vector2 (subtle różnice cross-platform). Adapter w Unity konwertuje
    /// do/z UnityEngine.Vector2.
    ///
    /// Layout: 8 bytes total, struct compatible z binary serialization (BinaryWriter/Reader).
    /// </summary>
    [Serializable]
    public struct GraphPoint : IEquatable<GraphPoint>
    {
        public float X;
        public float Y;

        public GraphPoint(float x, float y) { X = x; Y = y; }

        public static readonly GraphPoint Zero = new GraphPoint(0f, 0f);

        public float SqrMagnitude => X * X + Y * Y;
        public float Magnitude => (float)Math.Sqrt(SqrMagnitude);

        public static GraphPoint operator +(GraphPoint a, GraphPoint b) => new GraphPoint(a.X + b.X, a.Y + b.Y);
        public static GraphPoint operator -(GraphPoint a, GraphPoint b) => new GraphPoint(a.X - b.X, a.Y - b.Y);
        public static GraphPoint operator *(GraphPoint a, float s) => new GraphPoint(a.X * s, a.Y * s);
        public static GraphPoint operator /(GraphPoint a, float s) => new GraphPoint(a.X / s, a.Y / s);

        public static bool operator ==(GraphPoint a, GraphPoint b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(GraphPoint a, GraphPoint b) => !(a == b);

        public static float Distance(GraphPoint a, GraphPoint b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public static float SqrDistance(GraphPoint a, GraphPoint b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        public static float Dot(GraphPoint a, GraphPoint b) => a.X * b.X + a.Y * b.Y;

        public bool Equals(GraphPoint other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is GraphPoint p && Equals(p);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() << 1);
        public override string ToString() => $"({X:F2},{Y:F2})";
    }
}
