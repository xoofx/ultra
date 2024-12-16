// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Sampler;

public enum UltraSamplerThreadState
{
    // Matching TH_STATE from mach/thread_info.h

    Running = 1, /* thread is running normally */
    Stopped = 2, /* thread is stopped */
    Waiting = 3, /* thread is waiting normally */
    Uninterruptible = 4, /* thread is in an uninterruptible wait */
    Halted = 5 /* thread is halted at a clean point */
}