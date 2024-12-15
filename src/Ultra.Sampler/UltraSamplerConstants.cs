// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core;

public static class UltraSamplerConstants
{
    public const string ProviderName = "Ultra-Sampler";

    public const string IdAsString = "{04E4DCBF-494F-4A77-B55E-F5C041A92F56}";

    public static readonly Guid ProviderGuid = new(IdAsString);

    public const int NativeCallStackEventId = 1;

    public const int NativeModuleEventId = 2;
}