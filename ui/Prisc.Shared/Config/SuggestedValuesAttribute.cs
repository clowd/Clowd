using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Clowd.Config
{
    /// <summary>
    /// Marks a string settings property whose value is usually one of a known set (e.g. AWS region
    /// names) but may also be typed freehand. The settings UI renders it as an editable dropdown:
    /// the suggestions come from a public static method or property (returning
    /// <see cref="IEnumerable{String}"/>) named by <see cref="MemberName"/> on <see cref="SourceType"/>.
    /// Keeping the option source on the provider (rather than in the UI layer) lets provider
    /// libraries own their own vocabulary without the reflection-driven factory taking a dependency
    /// on them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class SuggestedValuesAttribute : Attribute
    {
        public Type SourceType { get; }
        public string MemberName { get; }

        public SuggestedValuesAttribute(Type sourceType, string memberName)
        {
            SourceType = sourceType;
            MemberName = memberName;
        }

        /// <summary>Resolves the suggestion list. Never throws — a broken reference just yields an
        /// empty list so the property still renders (as a plain, free-text field).</summary>
        public IReadOnlyList<string> GetValues()
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

                var method = SourceType.GetMethod(MemberName, flags, null, Type.EmptyTypes, null);
                object raw = method != null
                    ? method.Invoke(null, null)
                    : SourceType.GetProperty(MemberName, flags)?.GetValue(null);

                if (raw is IEnumerable<string> values)
                    return values.ToList();
            }
            catch
            {
                // fall through to the empty list
            }

            return Array.Empty<string>();
        }
    }
}
