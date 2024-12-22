// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// A generic list class used to store items of type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The type of items in the list.</typeparam>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(UGenericList<>.DebugListView))]
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
    /// Gets the number of items in the list.
    /// </summary>
    public int Count => List.Count;

    /// <summary>
    /// Gets the item at the specified index.
    /// </summary>
    /// <param name="index">The index of the item to get.</param>
    /// <returns>The item at the specified index</returns>
    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => List[index];
    }

    /// <summary>
    /// Gets the items in the list as a span.
    /// </summary>
    public ReadOnlySpan<T> Items => List.AsSpan();

    /// <inheritdoc />
    public override string ToString() => $"{GetType().Name}[{Count}]";

    /// <summary>
    /// Gets an enumerator for the items in the list.
    /// </summary>
    /// <returns>An enumerator for the items in the list.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ReadOnlySpan<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => List.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => List.GetEnumerator();

    [ExcludeFromCodeCoverage]
    private sealed class DebugListView(UGenericList<T> genericList)
    {
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items
        {
            get
            {
                T[] array = new T[genericList.Count];
                genericList.List.CopyTo((Span<T>)array);
                return array;
            }
        }
    }
}