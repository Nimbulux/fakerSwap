using System;

namespace FakerSwap.UI.NativeWindow
{
    /// <summary>
    /// Tiny fuzzy matcher for the window's list search boxes.
    ///
    /// Rules (case-insensitive):
    ///   * empty query matches everything,
    ///   * "#123" matches the numeric id containing 123,
    ///   * a plain numeric query also matches the id,
    ///   * otherwise: substring match, or a character subsequence match on any field
    ///     (so "tls" finds "True Love Song" and "天界" finds "天界区域").
    /// </summary>
    internal static class TextMatch
    {
        public static bool Matches(string query, int id, string fieldA, string fieldB)
        {
            if (string.IsNullOrEmpty(query)) return true;
            string q = query.Trim();
            if (q.Length == 0) return true;

            string idText = id.ToString();
            if (q[0] == '#')
            {
                string digits = q.Substring(1).Trim();
                return digits.Length > 0 && idText.IndexOf(digits, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            int parsed;
            if (int.TryParse(q, out parsed))
            {
                // A pure number searches ids (and falls through to the text fields too).
                if (idText.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            if (Contains(fieldA, q) || Contains(fieldB, q)) return true;
            return Subsequence(fieldA, q) || Subsequence(fieldB, q);
        }

        public static bool Matches(string query, string field)
        {
            if (string.IsNullOrEmpty(query)) return true;
            string q = query.Trim();
            if (q.Length == 0) return true;
            return Contains(field, q) || Subsequence(field, q);
        }

        private static bool Contains(string text, string query)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True when every char of <paramref name="query"/> appears in order in <paramref name="text"/>.</summary>
        private static bool Subsequence(string text, string query)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return false;
            int qi = 0;
            for (int i = 0; i < text.Length && qi < query.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(query[qi])) qi++;
            }
            return qi == query.Length;
        }
    }
}
