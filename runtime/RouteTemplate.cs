using System;
using System.Collections.Generic;

namespace UdpRelay
{
    /// <summary>
    /// A path template such as <c>/events/{event}/subscriber/{name}</c>.
    /// Literal segments match case-insensitively, a <c>{parameter}</c> segment captures one whole (unescaped) path segment.
    /// </summary>
    internal sealed class RouteTemplate
    {
        private readonly record struct Segment(string Value, bool IsParameter);

        private readonly Segment[] _segments;

        internal string Template { get; }

        internal RouteTemplate(string template)
        {
            if (string.IsNullOrWhiteSpace(template) || template[0] != '/')
                throw new ArgumentException($"Route template '{template}' must start with '/'.", nameof(template));

            var parts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);

            _segments = new Segment[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var isParameter = part.Length > 2 && part[0] == '{' && part[^1] == '}';
                var value = isParameter ? part[1..^1] : part;

                if (value.AsSpan().IndexOfAny('{', '}') >= 0)
                    throw new ArgumentException($"Route template '{template}' has a malformed segment '{part}'. A parameter must take up a whole segment, e.g. '{{name}}'.", nameof(template));

                if (isParameter && !parameterNames.Add(value))
                    throw new ArgumentException($"Route template '{template}' declares parameter '{value}' more than once.", nameof(template));

                _segments[i] = new Segment(value, isParameter);
            }

            Template = template;
        }

        /// <summary>
        /// Whether both templates match exactly the same paths, e.g. <c>/a/{x}</c> and <c>/A/{y}</c>.
        /// </summary>
        internal bool HasSameShapeAs(RouteTemplate other)
        {
            if (_segments.Length != other._segments.Length)
                return false;

            for (var i = 0; i < _segments.Length; i++)
            {
                var segment = _segments[i];
                var otherSegment = other._segments[i];
                if (segment.IsParameter != otherSegment.IsParameter)
                    return false;

                if (!segment.IsParameter && !string.Equals(segment.Value, otherSegment.Value, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        /// <param name="pathSegments">Unescaped path segments, see <see cref="SplitPath"/>.</param>
        /// <param name="routeValues">Parameter values by parameter name, if matched.</param>
        internal bool TryMatch(IReadOnlyList<string> pathSegments, out IReadOnlyDictionary<string, string> routeValues)
        {
            routeValues = null;
            if (pathSegments.Count != _segments.Length)
                return false;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < _segments.Length; i++)
            {
                var segment = _segments[i];
                if (segment.IsParameter)
                    values[segment.Value] = pathSegments[i];
                else if (!string.Equals(segment.Value, pathSegments[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            routeValues = values;
            return true;
        }

        /// <summary>
        /// Splits an escaped URL path (see <see cref="Uri.AbsolutePath"/>) into unescaped segments. Empty segments are skipped,
        /// so trailing and repeated slashes are ignored, while an escaped slash ("%2F") stays inside its segment.
        /// </summary>
        internal static string[] SplitPath(string path)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
                segments[i] = Uri.UnescapeDataString(segments[i]);

            return segments;
        }

        public override string ToString() => Template;
    }
}