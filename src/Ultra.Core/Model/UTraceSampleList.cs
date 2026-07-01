// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a list of <see cref="UTraceSample"/>.
/// </summary>
public sealed class UTraceSampleList : UGenericList<UTraceSample>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UTraceSampleList"/> class.
    /// </summary>
    public UTraceSampleList() : base(1024)
    {
    }

    /// <summary>
    /// Adds a new sample to the list.
    /// </summary>
    /// <param name="sample">The sample to add.</param>
    public void Add(UTraceSample sample) => List.Add(sample);
}