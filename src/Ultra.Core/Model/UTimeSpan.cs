// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a time span in the traced process.
/// </summary>
/// <remarks>This is used mainly to override the ToString() and print only in ms.</remarks>
public readonly record struct UTimeSpan(TimeSpan Value) : IComparable<UTimeSpan>, IComparable
{
    /// <summary>
    /// Gets the time span value in milliseconds.
    /// </summary>
    public double InMs => Value.TotalMilliseconds;

    /// <inheritdoc />
    public override string ToString() => $"{InMs:#,##0.0###}ms";

    /// <summary>
    /// Implicit conversion to <see cref="TimeSpan"/>.
    /// </summary>
    public static implicit operator TimeSpan(UTimeSpan timeSpan) => timeSpan.Value;

    /// <summary>
    /// Implicit conversion from <see cref="TimeSpan"/>.
    /// </summary>
    public static implicit operator UTimeSpan(TimeSpan value) => new(value);

    /// <summary>
    /// Creates a <see cref="UTimeSpan"/> from milliseconds.
    /// </summary>
    public static UTimeSpan FromMilliseconds(double timeInMs) => TimeSpan.FromMilliseconds(timeInMs);

    /// <inheritdoc />
    public int CompareTo(UTimeSpan other)
    {
        return Value.CompareTo(other.Value);
    }

    /// <inheritdoc />
    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        return obj is UTimeSpan other ? CompareTo(other) : throw new ArgumentException($"Object must be of type {nameof(UTimeSpan)}");
    }

    /// <summary>
    /// Compares two time spans for less than.
    /// </summary>
    public static bool operator <(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Compares two time spans for greater than.
    /// </summary>
    public static bool operator >(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Compares two time spans for less than or equal.
    /// </summary>
    public static bool operator <=(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Compares two time spans for greater than or equal.
    /// </summary>
    public static bool operator >=(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) >= 0;
    }
}