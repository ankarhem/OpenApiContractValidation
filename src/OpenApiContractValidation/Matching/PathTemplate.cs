using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace OpenApiContractValidation.Matching;

/// <summary>
/// A single OpenAPI path template (for example <c>/users/{id}</c>) compiled into a regular
/// expression that can match an incoming URL path and extract its path parameters.
/// </summary>
/// <remarks>
/// <para>
/// Templates are compiled per <see href="https://datatracker.ietf.org/doc/html/rfc6570">RFC 6570 Level 1</see>
/// semantics: a parameter segment <c>{name}</c> matches exactly one path segment and never spans a
/// <c>/</c>. Path matching is case-sensitive, as required for OpenAPI paths
/// (<see href="https://datatracker.ietf.org/doc/html/rfc3986">RFC 3986</see>), and trailing slashes are
/// not tolerated: the template <c>/users</c> matches <c>/users</c> but not <c>/users/</c>.
/// </para>
/// <para>
/// Each instance also exposes a <see cref="SpecificityKey"/> that ranks templates so that a literal
/// segment outranks a parameter segment, allowing matchers to prefer <c>/users/me</c> over
/// <c>/users/{id}</c> when both could match the same path.
/// </para>
/// </remarks>
public sealed class PathTemplate
{
    /// <summary>
    /// Regex that recognises a single OpenAPI parameter segment such as <c>{id}</c>.
    /// The captured <c>name</c> excludes braces; remaining RFC 6570 character restrictions are
    /// enforced implicitly by the surrounding segment structure.
    /// </summary>
    private static readonly Regex ParameterSegmentPattern = new(
        @"^\{(?<name>[^{}]+)\}$",
        RegexOptions.CultureInvariant
    );

    private const int ParameterKind = 0;
    private const int LiteralKind = 1;

    /// <summary>
    /// A shared, empty captures dictionary returned whenever a template with no parameters matches.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyCaptures =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(0, StringComparer.Ordinal)
        );

    private readonly Regex _compiled;
    private readonly string[] _parameterNames;

    /// <summary>
    /// Gets the original OpenAPI path template string this instance was compiled from.
    /// </summary>
    public string Template { get; }

    /// <summary>
    /// Gets the ordered list of path-parameter names declared in <see cref="Template"/>,
    /// or an empty collection when the template has no parameters.
    /// </summary>
    public IReadOnlyList<string> ParameterNames => _parameterNames;

    /// <summary>
    /// Gets a per-segment specificity key used to rank templates.
    /// </summary>
    /// <value>
    /// One integer per segment of <see cref="Template"/> (including the leading empty segment for a
    /// rooted template). <see cref="LiteralKind"/> (1) marks a literal segment and
    /// <see cref="ParameterKind"/> (0) marks a parameter segment; higher values rank earlier when
    /// compared left-to-right so that literal-heavy templates are preferred.
    /// </value>
    public IReadOnlyList<int> SpecificityKey { get; }

    /// <summary>
    /// Compiles an OpenAPI path template into a matcher.
    /// </summary>
    /// <param name="template">The OpenAPI path template, for example <c>/users/{id}</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is <see langword="null"/>.</exception>
    public PathTemplate(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        Template = template;

        var segments = template.Split('/');
        var patternParts = new string[segments.Length];
        var kinds = new int[segments.Length];
        var parameterNames = new List<string>(segments.Length);

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var match = ParameterSegmentPattern.Match(segment);
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                parameterNames.Add(name);
                // Use a numbered capture group so arbitrary RFC 6570 names never collide with
                // the .NET regex group-name grammar. Parameter names are re-mapped by ordinal.
                patternParts[i] = @"([^/]+)";
                kinds[i] = ParameterKind;
            }
            else
            {
                patternParts[i] = Regex.Escape(segment);
                kinds[i] = LiteralKind;
            }
        }

        _parameterNames = parameterNames.ToArray();
        SpecificityKey = kinds;

        var pattern = "^" + string.Join('/', patternParts) + "$";
        _compiled = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>
    /// Attempts to match <paramref name="path"/> against this template.
    /// </summary>
    /// <param name="path">The URL-decoded request path. The caller is responsible for decoding.</param>
    /// <param name="captures">
    /// When this method returns <see langword="true"/>, the captured path parameters keyed by their
    /// OpenAPI name; otherwise an empty dictionary.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="path"/> exactly matches this template; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public bool TryMatch(string path, out IReadOnlyDictionary<string, string> captures)
    {
        ArgumentNullException.ThrowIfNull(path);

        var match = _compiled.Match(path);
        if (!match.Success)
        {
            captures = EmptyCaptures;
            return false;
        }

        if (_parameterNames.Length == 0)
        {
            captures = EmptyCaptures;
            return true;
        }

        var result = new Dictionary<string, string>(_parameterNames.Length, StringComparer.Ordinal);
        for (var i = 0; i < _parameterNames.Length; i++)
        {
            // Groups[0] is the whole match; subsequent groups map 1:1 to declared parameters
            // because literal segments never emit capture groups.
            result[_parameterNames[i]] = match.Groups[i + 1].Value;
        }

        captures = result;
        return true;
    }
}
