using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// O8: minimal condition → sentence generator. Finite ruled grammar only —
    /// every user-visible fragment goes through <see cref="DisplayCopy"/> format
    /// methods (operator + source + value + lifetime). No free-form natural language.
    /// </summary>
    public static class ConditionSentence
    {
        /// <summary>
        /// Build a condition sentence from a schema <see cref="Condition"/> and optional
        /// <see cref="Lifetime"/>. When <paramref name="lifetime"/> is
        /// <see cref="LifetimeKind.OnChange"/>, the change-direction phrase is used
        /// (no level comparison). Null condition → empty.
        /// </summary>
        public static string From(
            Condition condition,
            Lifetime lifetime = null,
            AliasTable aliases = null)
        {
            if (condition == null)
                return string.Empty;

            string source = ResolveSource(condition.Source, aliases);

            // onChange lives on Lifetime, not on the condition operator.
            if (lifetime != null && lifetime.Kind == LifetimeKind.OnChange)
            {
                return DisplayCopy.ConditionChangeSentence(
                    source,
                    DisplayCopy.ChangeDirectionPhrase(lifetime.Direction));
            }

            var op = condition.Operator;
            if (op == null || op.Value == ConditionOperator.Unknown)
                return source;

            switch (op.Value)
            {
                case ConditionOperator.IsTrue:
                    return DisplayCopy.ConditionBoolSentence(source, DisplayCopy.OpIsOn);
                case ConditionOperator.IsFalse:
                    return DisplayCopy.ConditionBoolSentence(source, DisplayCopy.OpIsOff);
                case ConditionOperator.LessThan:
                case ConditionOperator.LessOrEqual:
                case ConditionOperator.GreaterThan:
                case ConditionOperator.GreaterOrEqual:
                case ConditionOperator.Equals:
                case ConditionOperator.NotEquals:
                {
                    string unit = ResolveUnit(condition.Source, aliases);
                    double value = condition.Value ?? 0;
                    return DisplayCopy.ConditionLevelSentence(
                        source,
                        DisplayCopy.OperatorPhrase(op.Value),
                        DisplayCopy.ConditionValue(value, unit));
                }
                default:
                    return source;
            }
        }

        /// <summary>Resolve a source to its alias phrase, or a leaf fallback.</summary>
        public static string ResolveSource(ValueSource source, AliasTable aliases)
        {
            if (source == null)
                return string.Empty;

            string name = source.Name ?? string.Empty;
            if (aliases != null && TryLookupAlias(aliases, source.Kind, name, out var entry))
                return entry.Alias ?? name;

            return LeafName(name);
        }

        /// <summary>Unit from the alias table, or null when unitless / unknown.</summary>
        public static string ResolveUnit(ValueSource source, AliasTable aliases)
        {
            if (source == null || aliases == null)
                return null;
            if (TryLookupAlias(aliases, source.Kind, source.Name, out var entry))
                return string.IsNullOrEmpty(entry.Unit) ? null : entry.Unit;
            return null;
        }

        private static bool TryLookupAlias(
            AliasTable table,
            ValueSourceKind kind,
            string name,
            out AliasEntry entry)
        {
            entry = null;
            if (table == null || string.IsNullOrEmpty(name))
                return false;

            var list = table.Aliases;
            if (list != null)
            {
                // Exact match preferred (kind-aware, then any kind).
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    if (a == null || !string.Equals(a.Ref, name, StringComparison.Ordinal))
                        continue;
                    if (kind == ValueSourceKind.BuiltIn && a.Kind != AliasKind.BuiltIn)
                        continue;
                    if (kind == ValueSourceKind.SimHubProperty && a.Kind != AliasKind.Property)
                        continue;
                    entry = a;
                    return true;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    if (a != null && string.Equals(a.Ref, name, StringComparison.Ordinal))
                    {
                        entry = a;
                        return true;
                    }
                }
            }

            // Pattern rules (regex).
            var patterns = table.PatternRules;
            if (patterns != null)
            {
                for (int i = 0; i < patterns.Count; i++)
                {
                    var p = patterns[i];
                    if (p == null || string.IsNullOrEmpty(p.Match))
                        continue;
                    try
                    {
                        var m = Regex.Match(name, p.Match);
                        if (!m.Success)
                            continue;
                        entry = new AliasEntry
                        {
                            Alias = ExpandPattern(p.AliasPattern, m),
                            Unit = p.Unit,
                        };
                        return true;
                    }
                    catch (ArgumentException)
                    {
                        // Bad pattern in catalog — skip.
                    }
                }
            }

            // Prefix rules.
            var prefixes = table.PrefixRules;
            if (prefixes != null)
            {
                for (int i = 0; i < prefixes.Count; i++)
                {
                    var p = prefixes[i];
                    if (p == null || string.IsNullOrEmpty(p.Prefix))
                        continue;
                    if (!name.StartsWith(p.Prefix, StringComparison.Ordinal))
                        continue;
                    string rest = name.Substring(p.Prefix.Length);
                    entry = new AliasEntry
                    {
                        Alias = (p.AliasPattern ?? "$1").Replace("$1", rest),
                        Unit = p.Unit,
                    };
                    return true;
                }
            }

            return false;
        }

        private static string ExpandPattern(string pattern, Match m)
        {
            if (string.IsNullOrEmpty(pattern))
                return m.Value;
            string result = pattern;
            for (int g = 1; g < m.Groups.Count; g++)
            {
                string token = "$" + g.ToString(CultureInfo.InvariantCulture);
                // Drop leading zeros on numeric captures (FN layer style).
                string capture = m.Groups[g].Value;
                if (int.TryParse(capture, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    capture = n.ToString(CultureInfo.InvariantCulture);
                result = result.Replace(token, capture);
            }
            return result;
        }

        /// <summary>Last-dot leaf of a dotted path, or the name itself.</summary>
        private static string LeafName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            int dot = name.LastIndexOf('.');
            return dot >= 0 && dot < name.Length - 1 ? name.Substring(dot + 1) : name;
        }
    }
}
