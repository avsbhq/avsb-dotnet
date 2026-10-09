// IsExternalInit.cs
// netstandard2.1 predates C# 9, so the marker type the compiler needs for records and init-only
// setters is missing from that framework, and every `init` accessor failed to compile there.
// Declaring it internally is the supported way to use them on an older target. net5.0 and later
// ship the type, so this compiles only below that.

#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
