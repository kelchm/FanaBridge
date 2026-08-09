// net48 polyfill for the C# 9 module-initializer attribute (compiler-only; type missing on TFM).
// Exception to directory/namespace literalness: System.Runtime.CompilerServices by definition.
#pragma warning disable IDE0130 // Polyfill attribute must live in System.Runtime.CompilerServices by definition
using System;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
