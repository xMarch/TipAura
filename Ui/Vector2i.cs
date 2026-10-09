// Integer screen/client coordinates, independent of the windowing library.
internal readonly record struct Vector2i(int X, int Y)
{
    public static readonly Vector2i Zero = default;
    public static Vector2i operator +(Vector2i a, Vector2i b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2i operator -(Vector2i a, Vector2i b) => new(a.X - b.X, a.Y - b.Y);
}
