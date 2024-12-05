// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Sampler.MacOS;

internal enum NativeModuleEventKind
{
    AlreadyLoaded = 0,
    Loaded = 1,
    Unloaded = 2
}