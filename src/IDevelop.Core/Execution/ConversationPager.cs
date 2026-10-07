using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDevelop.Execution;

internal static class ConversationPager
{
    private sealed record Position(AttemptId Attempt, long Order, long Line, int Slot, string Entry);
    private sealed record CursorToken(string Scope, string Chain, Position Position);
    private sealed record WindowToken(string Scope, string Chain, Position First, Position Last, bool Empty);
    private static readonly JsonSerializerOptions Options = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static HistoryResult Read(string scope, IReadOnlyList<AttemptHistory> chain, long revision, HistoryQuery query, int count)
    {
        if (count <= 0)
        {
            return new HistoryResult.Unavailable("The history page size must be positive.");
        }

        if (chain.Count == 0)
        {
            return new HistoryResult.Unavailable("The selected attempt has no readable history.");
        }

        var chainId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("/", chain.Select(attempt => attempt.Id.ToString())))));
        var attemptOrder = chain.Select((attempt, index) => (attempt.Id, index)).ToDictionary(item => item.Id, item => item.index);
        var rows = chain.SelectMany(attempt => attempt.Rows).ToArray();
        var byId = rows.ToDictionary(row => row.Entry.Id.Value, StringComparer.Ordinal);
        Position At(ProjectedConversationEntry row) => new(row.Entry.Turn.Attempt, row.Entry.Order, row.Position, row.Slot, row.Entry.Id.Value);
        Position Resolve(Position position) => byId.TryGetValue(position.Entry, out var row) ? At(row) : position;
        int Compare(Position left, Position right)
        {
            var comparison = attemptOrder[left.Attempt].CompareTo(attemptOrder[right.Attempt]);
            if (comparison != 0) return comparison;
            comparison = left.Order.CompareTo(right.Order);
            if (comparison != 0) return comparison;
            comparison = left.Line.CompareTo(right.Line);
            if (comparison != 0) return comparison;
            comparison = left.Slot.CompareTo(right.Slot);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Entry, right.Entry);
        }

        bool Valid(Position? position) => position is not null && attemptOrder.ContainsKey(position.Attempt)
            && position.Order >= 0 && position.Line >= 0 && position.Slot >= 0 && !string.IsNullOrEmpty(position.Entry)
            && position.Entry.StartsWith($"c1/{position.Attempt}/", StringComparison.Ordinal)
            && (byId.TryGetValue(position.Entry, out var row) && row.Entry.Turn.Attempt == position.Attempt && row.Entry.Order == position.Order
                || rows.Length == 0 && position.Entry == $"c1/{chain[0].Id}/empty" && position.Order == 0 && position.Line == 0 && position.Slot == 0);
        bool Owned(string tokenScope, string tokenChain) => tokenScope == scope && tokenChain == chainId;
        var fallback = rows.Length > 0 ? At(rows[0]) : new Position(chain[0].Id, 0, 0, 0, $"c1/{chain[0].Id}/empty");
        ProjectedConversationEntry[] selected;
        Position first;
        Position last;
        switch (query)
        {
            case HistoryQuery.Latest:
                selected = rows.TakeLast(count).ToArray();
                first = selected.Length > 0 ? At(selected[0]) : fallback;
                last = selected.Length > 0 ? At(selected[^1]) : fallback;
                break;
            case HistoryQuery.Before:
            case HistoryQuery.After:
                var cursor = query is HistoryQuery.Before b ? b.Cursor : ((HistoryQuery.After)query).Cursor;
                var token = Decode<CursorToken>(cursor.Value, "hc1.");
                if (token is null || !Valid(token.Position))
                {
                    return new HistoryResult.Unavailable("The history cursor is invalid.");
                }

                if (!Owned(token.Scope, token.Chain))
                {
                    return new HistoryResult.Unavailable("The history cursor belongs to another conversation or attempt chain.");
                }

                var anchor = Resolve(token.Position);
                selected = query is HistoryQuery.Before
                    ? rows.Where(row => Compare(At(row), anchor) < 0).TakeLast(count).ToArray()
                    : rows.Where(row => Compare(At(row), anchor) > 0).Take(count).ToArray();
                first = selected.Length > 0 ? At(selected[0]) : anchor;
                last = selected.Length > 0 ? At(selected[^1]) : anchor;
                break;
            case HistoryQuery.AroundRequest around:
                var request = Array.FindIndex(rows, row => row.Entry.Content is ConversationContent.Request r && r.Key == around.Request);
                if (request < 0)
                {
                    return new HistoryResult.Unavailable("The request has no persisted entry in the selected attempt chain. Select its attempt to read it.");
                }

                var start = Math.Max(0, Math.Min(request - count / 2, rows.Length - count));
                selected = rows.Skip(start).Take(count).ToArray();
                first = At(selected[0]);
                last = At(selected[^1]);
                break;
            case HistoryQuery.RefreshWindow refresh:
                var window = Decode<WindowToken>(refresh.Window.Value, "hw1.");
                if (window is null || !Valid(window.First) || !Valid(window.Last))
                {
                    return new HistoryResult.Unavailable("The history window is invalid.");
                }

                if (!Owned(window.Scope, window.Chain))
                {
                    return new HistoryResult.Unavailable("The history window belongs to another conversation or attempt chain.");
                }

                first = Resolve(window.First);
                last = Resolve(window.Last);
                if (Compare(first, last) > 0)
                {
                    return new HistoryResult.Unavailable("The history window has reversed position bounds.");
                }

                selected = window.Empty ? [] : rows.Where(row => Compare(At(row), first) >= 0 && Compare(At(row), last) <= 0).ToArray();
                break;
            default:
                return new HistoryResult.Unavailable("The history query is unsupported.");
        }

        return new HistoryResult.Page(revision, [.. selected.Select(row => row.Entry)],
            new HistoryWindow(Encode("hw1.", new WindowToken(scope, chainId, first, last, selected.Length == 0))),
            new HistoryCursor(Encode("hc1.", new CursorToken(scope, chainId, first))),
            new HistoryCursor(Encode("hc1.", new CursorToken(scope, chainId, last))),
            rows.Any(row => Compare(At(row), first) < 0), rows.Any(row => Compare(At(row), last) > 0));
    }

    private static string Encode<T>(string prefix, T token) => prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(token, Options))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static T? Decode<T>(string? value, string prefix) where T : class
    {
        if (value is null || value.Length > 16384 || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var encoded = value[prefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(encoded), Options);
        }
        catch (Exception e) when (e is FormatException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
