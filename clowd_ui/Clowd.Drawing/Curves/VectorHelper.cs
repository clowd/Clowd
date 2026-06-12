// Copyright (c) 2015 burningmime
//
// This software is provided 'as-is', without any express or implied
// warranty. In no event will the authors be held liable for any damages
// arising from the use of this software.
//
// Permission is granted to anyone to use this software for any purpose,
// including commercial applications, and to alter it and redistribute it
// freely, subject to the following restrictions:
//
// 1. The origin of this software must not be misrepresented; you must not
//    claim that you wrote the original software. If you use this software
//    in a product, an acknowledgement in the product documentation would be
//    appreciated but is not required.
// 2. Altered source versions must be plainly marked as such, and must not be
//    misrepresented as being the original software.
// 3. This notice may not be removed or altered from any source distribution.

// Modified for Clowd: vector type fixed to Avalonia.Vector / System.Double (the #if vector-type
// ladder from the original source has been removed).

using System.Runtime.CompilerServices;

using VECTOR = Avalonia.Vector;
using FLOAT = System.Double;

namespace Clowd.Drawing.Curves
{
    /// <summary>
    /// The point of this class is to abstract some of the functions of Vector2 so they can be used with System.Windows.Vector,
    /// System.Numerics.Vector2, UnityEngine.Vector2, or another vector type.
    /// </summary>
    public static class VectorHelper
    {
        /// <summary>
        /// Below this, don't trust the results of floating point calculations.
        /// </summary>
        public const FLOAT EPSILON = 1.2e-12f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static FLOAT Distance(VECTOR a, VECTOR b) { return (a - b).Length; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static FLOAT DistanceSquared(VECTOR a, VECTOR b) { return (a - b).SquaredLength; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static FLOAT Dot(VECTOR a, VECTOR b) { return a.X * b.X + a.Y * b.Y; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static VECTOR Normalize(VECTOR v) { return v.Normalize(); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static FLOAT Length(VECTOR v) { return v.Length; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static FLOAT LengthSquared(VECTOR v) { return v.SquaredLength; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static VECTOR Lerp(VECTOR a, VECTOR b, FLOAT amount) { return new VECTOR(a.X + ((b.X - a.X) * amount), a.Y + ((b.Y - a.Y) * amount)); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static FLOAT GetX(VECTOR v) { return v.X; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static FLOAT GetY(VECTOR v) { return v.Y; }

        /// <summary>
        /// Checks if two vectors are equal within a small bounded error.
        /// </summary>
        /// <param name="v1">First vector to compare.</param>
        /// <param name="v2">Second vector to compare.</param>
        /// <returns>True iff the vectors are almost equal.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool EqualsOrClose(VECTOR v1, VECTOR v2)
        {
            return DistanceSquared(v1, v2) < EPSILON;
        }
    }
}
