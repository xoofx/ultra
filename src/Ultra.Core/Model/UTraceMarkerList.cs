// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a list of <see cref="UTraceMarker"/>.
/// </summary>
public sealed class UTraceMarkerList : UGenericList<UTraceMarker>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UTraceMarkerList"/> class.
    /// </summary>
    public UTraceMarkerList() : base()
    {
    }

    /// <summary>
    /// Adds a new marker to the list.
    /// </summary>
    /// <param name="marker">The sample to add.</param>
    public void Add(UTraceMarker marker) => List.Add(marker);
}