using System;

namespace StarTruckMP.Common;

public struct Vector3f
{
    public float X, Y, Z;
    public Vector3f(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static Vector3f Zero => new(0, 0, 0);
    public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
    public static Vector3f operator +(Vector3f a, Vector3f b) => new(a.X+b.X, a.Y+b.Y, a.Z+b.Z);
    public static Vector3f operator -(Vector3f a, Vector3f b) => new(a.X-b.X, a.Y-b.Y, a.Z-b.Z);
    public static bool operator ==(Vector3f a, Vector3f b) => a.X==b.X && a.Y==b.Y && a.Z==b.Z;
    public static bool operator !=(Vector3f a, Vector3f b) => !(a==b);
    public override bool Equals(object obj) => obj is Vector3f o && this==o;
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
}
