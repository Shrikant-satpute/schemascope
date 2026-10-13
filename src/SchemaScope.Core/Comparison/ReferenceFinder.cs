using System.Diagnostics;

namespace SchemaScope.Core.Comparison;

/// <summary>One object's text, ready to be searched. Deliberately free of model types.</summary>
public readonly record struct ScannedObject(
    string Key, string Schema, string Name, string TypeLabel, string Text);

/// <summary>One database's worth of objects to search.</summary>
public sealed class ScanScope
{
    /// <summary>"source", or the target's index as a string.</summary>
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public IReadOnlyList<ScannedObject> Objects { get; init; } = [];
}

public sealed class ReferenceLine
{
    public int Line { get; set; }
    public int Column { get; set; }
    /// <summary>The whole line, trimmed and capped - enough to recognise the call.</summary>
    public string Text { get; set; } = "";
}

public sealed class ReferenceObject
{
    public string Key { get; set; } = "";
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public string TypeLabel { get; set; } = "";
    /// <summary>True when this object <em>is</em> the thing being searched for.</summary>
    public bool IsDefinition { get; set; }
    public int MatchCount { get; set; }
    public List<ReferenceLine> Lines { get; set; } = [];
}

public sealed class ReferenceDatabase
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public int ObjectCount { get; set; }
    public int MatchCount { get; set; }
    public List<ReferenceObject> Objects { get; set; } = [];
}

public sealed class ReferenceResult
{
    public string Name { get; set; } = "";
    /// <summary>usedBy | uses</summary>
    public string Mode { get; set; } = "usedBy";
    public List<ReferenceDatabase> Databases { get; set; } = [];
    /// <summary>Set when a cap was hit, so the UI can say so instead of quietly showing less.</summary>
    public bool Truncated { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Where is this used?
///
/// A plain case-insensitive text scan over every object body, not a T-SQL parser -
/// on purpose. A parser cannot see a call assembled inside a string, and this
/// platform is full of dynamic SQL. Text finds <c>EXEC('… kn_X …')</c> exactly as
/// easily as <c>EXEC dbo.kn_X</c>. It is the same thing you would do with a search
/// box, done over the schema already held in memory.
///
/// The trade-off, stated plainly: a match inside a comment counts as a use. That is
/// the right way round - better to show a line the reader can dismiss in a second
/// than to hide a call that mattered.
/// </summary>
public static class ReferenceFinder
{
    /// <summary>Searching "id" would match everything and help nobody.</summary>
    public const int MinimumNameLength = 3;

    private const int MaxObjectsPerDatabase = 200;
    private const int MaxLinesPerObject = 20;
    private const int MaxLineLength = 200;

    /// <summary>
    /// T-SQL identifiers keep their underscores, and @variables and #temp tables
    /// are one word too - so kn_X must not match inside kn_X_Archive.
    /// </summary>
    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    /// <summary>Every object, in every database, whose body mentions this name.</summary>
    public static ReferenceResult UsedBy(string name, IReadOnlyList<ScanScope> scopes)
    {
        var clock = Stopwatch.StartNew();
        var result = new ReferenceResult { Name = name, Mode = "usedBy" };

        if (name.Length < MinimumNameLength) return result;

        foreach (var scope in scopes)
        {
            var database = new ReferenceDatabase { Id = scope.Id, Label = scope.Label };

            foreach (var candidate in scope.Objects)
            {
                // Nothing more will be shown, so nothing more is worth scanning.
                if (database.Objects.Count >= MaxObjectsPerDatabase)
                {
                    result.Truncated = true;
                    break;
                }

                var lines = FindLines(candidate.Text, name, out var matches);
                if (matches == 0) continue;

                database.Objects.Add(new ReferenceObject
                {
                    Key = candidate.Key,
                    Schema = candidate.Schema,
                    Name = candidate.Name,
                    TypeLabel = candidate.TypeLabel,
                    IsDefinition = string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase),
                    MatchCount = matches,
                    Lines = lines
                });

                database.MatchCount += matches;
            }

            // The definition first, then the callers by name - the same order every
            // time, so a second search of the same thing reads the same way.
            database.Objects = [.. database.Objects
                .OrderByDescending(o => o.IsDefinition)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)];

            database.ObjectCount = database.Objects.Count;
            result.Databases.Add(database);
        }

        result.ElapsedMs = clock.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// The other direction: which known objects does this one call?
    ///
    /// Its body is chopped into words once, then checked against the list of object
    /// names - so the cost is one pass over the text rather than one search per name.
    /// </summary>
    /// <param name="ignoreTypeLabels">
    /// Object types not worth reporting as a dependency. In practice the schema:
    /// every object mentions <c>dbo</c>, so listing it is noise in every single row.
    /// </param>
    public static ReferenceResult Uses(
        string objectKey, IReadOnlyList<ScanScope> scopes, IReadOnlySet<string>? ignoreTypeLabels = null)
    {
        var clock = Stopwatch.StartNew();
        var result = new ReferenceResult { Mode = "uses" };

        foreach (var scope in scopes)
        {
            var database = new ReferenceDatabase { Id = scope.Id, Label = scope.Label };

            var subject = scope.Objects.FirstOrDefault(o => o.Key == objectKey);
            if (subject.Key is null || string.IsNullOrEmpty(subject.Text))
            {
                result.Databases.Add(database);
                continue;
            }

            result.Name = subject.Name;
            var words = Words(subject.Text);

            foreach (var other in scope.Objects)
            {
                // Nothing more will be shown, so nothing more is worth scanning.
                if (database.Objects.Count >= MaxObjectsPerDatabase)
                {
                    result.Truncated = true;
                    break;
                }

                if (other.Key == objectKey) continue;
                if (other.Name.Length < MinimumNameLength) continue;
                if (ignoreTypeLabels?.Contains(other.TypeLabel) == true) continue;
                if (!words.Contains(other.Name)) continue;

                var lines = FindLines(subject.Text, other.Name, out var matches);
                if (matches == 0) continue;

                database.Objects.Add(new ReferenceObject
                {
                    Key = other.Key,
                    Schema = other.Schema,
                    Name = other.Name,
                    TypeLabel = other.TypeLabel,
                    MatchCount = matches,
                    Lines = lines
                });

                database.MatchCount += matches;
            }

            database.Objects = [.. database.Objects.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)];
            database.ObjectCount = database.Objects.Count;
            result.Databases.Add(database);
        }

        result.ElapsedMs = clock.ElapsedMilliseconds;
        return result;
    }

    /// <summary>Every identifier-shaped word in a body, case-insensitively.</summary>
    private static HashSet<string> Words(string text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var word = i < text.Length && IsWordChar(text[i]);

            if (word && start < 0) start = i;
            else if (!word && start >= 0)
            {
                if (i - start >= MinimumNameLength) words.Add(text[start..i]);
                start = -1;
            }
        }

        return words;
    }

    /// <summary>
    /// Whole-word matches, with the line numbers of the very text the diff shows -
    /// so "line 21" in the tree really is line 21 in the editor.
    /// </summary>
    private static List<ReferenceLine> FindLines(string text, string name, out int matchCount)
    {
        var found = new List<ReferenceLine>();
        matchCount = 0;

        if (string.IsNullOrEmpty(text) || name.Length == 0) return found;

        var from = 0;
        var line = 1;
        var lineStart = 0;
        var counted = 0;

        while (from <= text.Length - name.Length)
        {
            var hit = text.IndexOf(name, from, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;

            var boundedLeft = hit == 0 || !IsWordChar(text[hit - 1]);
            var end = hit + name.Length;
            var boundedRight = end >= text.Length || !IsWordChar(text[end]);

            if (boundedLeft && boundedRight)
            {
                matchCount++;

                // Matches arrive in order, so newlines are only ever counted once.
                for (var i = counted; i < hit; i++)
                {
                    if (text[i] != '\n') continue;
                    line++;
                    lineStart = i + 1;
                }
                counted = hit;

                if (found.Count < MaxLinesPerObject)
                {
                    var lineEnd = text.IndexOf('\n', hit);
                    if (lineEnd < 0) lineEnd = text.Length;

                    var body = text[lineStart..lineEnd].TrimEnd('\r').Trim();
                    if (body.Length > MaxLineLength) body = body[..MaxLineLength] + "...";

                    found.Add(new ReferenceLine
                    {
                        Line = line,
                        Column = hit - lineStart + 1,
                        Text = body
                    });
                }
            }

            from = hit + 1;
        }

        return found;
    }
}
