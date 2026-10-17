using System.Globalization;
using System.Text;

namespace Echoplex.Services;

/// <summary>
/// Búsqueda que perdona: sin acentos ni mayúsculas («beyonce» encuentra «Beyoncé»), sin signos («acdc» encuentra
/// «AC/DC») y con alguna errata («beatels» encuentra «The Beatles», «metalica» encuentra «Metallica»). Cada palabra
/// buscada tiene que aparecer en algún campo, y los resultados se ordenan por lo bien que encajan: primero lo que es
/// exactamente eso, luego lo que empieza así, lo que lo contiene y, al final, lo que se parece.
/// </summary>
public sealed class SearchQuery
{
    private readonly string[] _terms;

    public SearchQuery(string query)
    {
        _terms = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public bool IsEmpty => _terms.Length == 0;

    /// <summary>
    /// Cuánto encaja (0 = no encaja). <paramref name="fields"/>: los textos con su peso (el título pesa más que la ruta).
    /// </summary>
    public double Score(params (string text, double weight)[] fields) =>
        ScoreFolded(fields.Select(f => (Fold(f.text), f.weight)).ToArray());

    /// <summary>Como <see cref="Score"/>, con los textos ya pasados por <see cref="Fold"/> (para no repetirlo en cada tecla).</summary>
    public double ScoreFolded(params (string words, double weight)[] folded)
    {
        if (_terms.Length == 0) return 0;
        double total = 0;
        foreach (var term in _terms)
        {
            double best = 0;
            foreach (var (words, weight) in folded)
            {
                double q = Quality(term, words);
                if (q * weight > best) best = q * weight;
            }
            if (best == 0) return 0; // cada palabra tiene que estar en algún sitio
            total += best;
        }
        // un campo que es exactamente lo buscado (el título entero, el artista entero) va primero
        string whole = string.Join(' ', _terms);
        foreach (var (words, weight) in folded)
            if (words == whole) total += 50 * weight;
        return total;
    }

    /// <summary>Lo bien que está una palabra en un texto: palabra exacta, principio de palabra, dentro, o parecida.</summary>
    private static double Quality(string term, string text)
    {
        if (text.Length == 0) return 0;
        int at = text.IndexOf(term, StringComparison.Ordinal);
        if (at >= 0)
        {
            bool start = at == 0 || text[at - 1] == ' ';
            bool end = at + term.Length == text.Length || text[at + term.Length] == ' ';
            return start && end ? 10 : start ? 7 : 4;
        }
        // sin espacios ni signos: «acdc» en «ac dc», «ledzeppelin» en «led zeppelin»
        if (term.Length >= 3 && text.Replace(" ", "").Contains(term, StringComparison.Ordinal)) return 3;
        // erratas: una letra de más, de menos, cambiada o dos cambiadas de sitio (dos en palabras largas)
        if (term.Length < 4) return 0;
        int allowed = term.Length >= 8 ? 2 : 1;
        foreach (var word in text.Split(' '))
        {
            if (word.Length < 3) continue;
            if (Distance(term, word, allowed) <= allowed) return 2;
            // también al principio de una palabra más larga («metal» → «metallica»)
            if (word.Length > term.Length && Distance(term, word[..term.Length], allowed) <= allowed) return 1.5;
        }
        return 0;
    }

    /// <summary>Distancia de Damerau-Levenshtein (con trasposiciones), cortando en cuanto pasa de <paramref name="max"/>.</summary>
    private static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int rowMin = int.MaxValue;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (rowMin > max) return max + 1;
        }
        return d[a.Length, b.Length];
    }

    /// <summary>Minúsculas, sin acentos y con los signos convertidos en espacios (uno solo entre palabras).</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = true;
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().TrimEnd();
    }
}
