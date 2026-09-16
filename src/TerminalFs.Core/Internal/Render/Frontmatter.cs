using System.Globalization;
using System.Text;

namespace TerminalFs.Core.Internal.Render;

/// <summary>
/// Writes the YAML frontmatter block every page of this tree carries.
/// </summary>
/// <remarks>
/// The shape is the Open Knowledge Format's: a concept per markdown file, <c>index.md</c> at each
/// level for an agent descending the tree, and a small block of frontmatter in which <c>type</c>
/// is the one required field. Everything else here — <c>title</c>, <c>description</c>,
/// <c>tags</c>, <c>timestamp</c> — is OKF's recommended set, and the fields this tree adds of
/// its own (<c>command</c>, <c>status</c>, <c>shell</c>) are the producer-defined extras
/// the format leaves open.
/// </remarks>
internal sealed class Frontmatter
{
    private readonly List<KeyValuePair<string, string>> _fields = [];

    /// <summary>Starts a block for a concept of the given OKF <paramref name="type"/>.</summary>
    internal Frontmatter(string type) => Add("type", type);

    /// <summary>Adds a scalar field, skipping it when the value is absent.</summary>
    internal Frontmatter Add(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _fields.Add(new KeyValuePair<string, string>(name, Scalar(value)));
        }

        return this;
    }

    /// <summary>Adds a flow-sequence field, skipping it when there is nothing to list.</summary>
    internal Frontmatter AddList(string name, IEnumerable<string> values)
    {
        string[] items = [.. values.Where(value => !string.IsNullOrWhiteSpace(value))];

        if (items.Length > 0)
        {
            _fields.Add(new KeyValuePair<string, string>(
                name,
                "[" + string.Join(", ", items.Select(Scalar)) + "]"));
        }

        return this;
    }

    /// <summary>
    /// Adds the ISO 8601 timestamp OKF asks for: the moment this page is describing, which for
    /// a command is when it last changed. A clock read during rendering would say when the page
    /// happened to be read rather than when the thing it describes happened, which is worse than
    /// no field at all.
    /// </summary>
    internal Frontmatter AddTimestamp(DateTimeOffset when) =>
        Add("timestamp", when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

    /// <summary>The block, delimiters included, ending in a blank line.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder("---\n");

        foreach ((string name, string value) in _fields)
        {
            builder.Append(name).Append(": ").Append(value).Append('\n');
        }

        return builder.Append("---\n\n").ToString();
    }

    /// <summary>
    /// Quotes a scalar when YAML would otherwise read it as something else. Command text brings
    /// colons, braces, quotes and leading symbols into these fields as a matter of course, and an
    /// unquoted <c>{</c> starts a flow mapping.
    /// </summary>
    private static string Scalar(string value)
    {
        string flat = value.ReplaceLineEndings(" ").Trim();

        bool needsQuotes = flat.Length == 0
            || flat.Contains(": ", StringComparison.Ordinal)
            || flat.EndsWith(':')
            || flat.Contains(" #", StringComparison.Ordinal)
            || "[]{}>|*&!%@`,\"'#".Contains(flat[0], StringComparison.Ordinal)
            || flat[0] is '-' or '?' or ':' or ' '
            || flat[^1] == ' '
            || IsNotAString(flat);

        if (!needsQuotes)
        {
            return flat;
        }

        var quoted = new StringBuilder(flat.Length + 2).Append('"');

        foreach (char c in flat)
        {
            quoted.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",

                // A control character is not legal as itself even inside quotes, and a command
                // documentation from an assembly this program did not build can carry one.
                _ when char.IsControl(c) => $"\\x{(int)c:x2}",
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>
    /// Whether YAML would read this as a boolean, a number or a null rather than as text. A
    /// consumer asking for the <c>title</c> of a page would then be handed <c>true</c> or
    /// <c>1.0</c> — not a string, and in a typed reader not even the same kind of thing.
    /// </summary>
    private static bool IsNotAString(string value) =>
        Booleans.Contains(value)
        || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Every spelling YAML 1.1 reads as a boolean or a null, which is more than <c>true</c> and
    /// <c>false</c>: <c>y</c>, <c>no</c> and <c>on</c> are among them, and the last is a plausible
    /// member name.
    /// </summary>
    private static readonly HashSet<string> Booleans = new(StringComparer.OrdinalIgnoreCase)
    {
        "y", "yes", "n", "no", "true", "false", "on", "off", "null", "~",
    };
}
