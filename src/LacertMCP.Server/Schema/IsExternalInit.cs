// Polyfill for the C# 9+ `init` accessor on net48 / netstandard2.0.
//
// The C# compiler emits `modreq(IsExternalInit)` on every `init` setter — and on
// `record`-synthesized property setters. The type is shipped by .NET 5+ but
// absent on net48, so any project targeting net48 that uses records or `init`
// must supply the type itself.
//
// Defining it under <c>System.Runtime.CompilerServices</c> is the convention
// blessed by Microsoft and accepted by the compiler at lookup time.

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
