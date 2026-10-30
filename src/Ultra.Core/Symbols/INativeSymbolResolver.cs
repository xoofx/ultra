// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Model;

namespace Ultra.Core.Symbols;

/// <summary>
/// Resolves the code addresses of a native module to symbols (Mach-O, ELF, kernel kallsyms).
/// </summary>
internal interface INativeSymbolResolver
{
    /// <summary>
    /// Tries to resolve an address of the specified loaded module.
    /// </summary>
    /// <param name="module">The loaded module containing the address.</param>
    /// <param name="address">The address to resolve.</param>
    /// <param name="symbolAddress">The address identifying the symbol (used to aggregate the frames of a same function).</param>
    /// <param name="symbolName">The symbol name.</param>
    /// <returns><c>true</c> if a symbol was found.</returns>
    bool TryResolve(UTraceNativeModule module, ulong address, out ulong symbolAddress, out string symbolName);
}
