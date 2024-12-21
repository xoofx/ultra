// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections;
using System.ComponentModel;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// A generic list class used to store items of type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The type of items in the list.</typeparam>
public abstract class UGenericList<T>(int capacity) : IEnumerable<T>
{
    /// <summary>
    /// The list of items.
    /// </summary>
    protected UnsafeList<T> List = new(capacity);

    /// <summary>
    /// Initializes a new instance of the <see cref="UGenericList{T}"/> class.
    /// </summary>
    protected UGenericList() : this(0)
    {
    }

    /// <summary>
    /// Gets the items in the list as a span.
    /// </summary>
    public ReadOnlySpan<T> Items => List.AsSpan();


    /// <summary>
    /// Gets an enumerator for the items in the list.
    /// </summary>
    /// <returns>An enumerator for the items in the list.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ReadOnlySpan<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => List.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => List.GetEnumerator();
}