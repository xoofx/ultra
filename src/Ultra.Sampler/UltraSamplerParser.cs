// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Sampler;

public static class UltraSamplerParser
{
    public const string Name = "Ultra-Sampler";

    public const string IdAsString = "04E4DCBF-494F-4A77-B55E-F5C041A92F56";

    public static readonly Guid Id = new(IdAsString);

    public const int NativeCallStackEvent = 1;

    public const int NativeModuleEvent = 2;
}