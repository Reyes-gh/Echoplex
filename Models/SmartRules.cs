namespace Echoplex.Models;

/// <summary>
/// Reglas de una playlist inteligente: de dónde salen las canciones, qué condiciones cumplen, en qué orden y cuántas.
/// La lista se rehace sola cada vez que se abre o suena (y al cambiar la biblioteca).
/// </summary>
public sealed class SmartRules
{
    public const string ScopeAll = "all", ScopeArtist = "artist", ScopeGenre = "genre", ScopeFolder = "folder";
    public const string OrderPlays = "plays", OrderLeast = "least", OrderRecent = "recent", OrderAdded = "added",
        OrderRandom = "random", OrderAlbum = "album";

    /// <summary>Toda la biblioteca, un artista, un género o una carpeta (<see cref="ScopeValue"/>).</summary>
    public string Scope { get; set; } = ScopeAll;
    public string? ScopeValue { get; set; }
    public bool FavoritesOnly { get; set; }
    /// <summary>Sin escuchar en los últimos N días (0 = da igual).</summary>
    public int NotPlayedDays { get; set; }
    /// <summary>Escuchadas al menos N veces (0 = da igual).</summary>
    public int MinPlays { get; set; }
    /// <summary>Añadidas a la biblioteca en los últimos N días (0 = da igual).</summary>
    public int AddedDays { get; set; }
    public int YearFrom { get; set; }
    public int YearTo { get; set; }
    public string Order { get; set; } = OrderPlays;
    /// <summary>Como mucho N canciones (0 = todas).</summary>
    public int Limit { get; set; } = 50;

    public SmartRules Clone() => (SmartRules)MemberwiseClone();

    /// <summary>Las reglas en una frase, para la cabecera de la playlist.</summary>
    public string Describe()
    {
        var parts = new List<string>
        {
            Scope switch
            {
                ScopeArtist => $"de {ScopeValue}",
                ScopeGenre => $"del género {ScopeValue}",
                ScopeFolder => $"de la carpeta {System.IO.Path.GetFileName(ScopeValue ?? "")}",
                _ => "de toda tu música",
            },
        };
        if (FavoritesOnly) parts.Add("favoritas");
        if (NotPlayedDays > 0) parts.Add($"sin escuchar en {NotPlayedDays} días");
        if (MinPlays > 0) parts.Add($"escuchadas {MinPlays}+ veces");
        if (AddedDays > 0) parts.Add($"añadidas en los últimos {AddedDays} días");
        if (YearFrom > 0 || YearTo > 0) parts.Add(YearFrom > 0 && YearTo > 0 ? $"de {YearFrom} a {YearTo}" : YearFrom > 0 ? $"desde {YearFrom}" : $"hasta {YearTo}");
        parts.Add(Order switch
        {
            OrderLeast => "las menos escuchadas primero",
            OrderRecent => "las últimas escuchadas primero",
            OrderAdded => "las más nuevas primero",
            OrderRandom => "en orden aleatorio",
            OrderAlbum => "por artista y álbum",
            _ => "las más escuchadas primero",
        });
        if (Limit > 0) parts.Add($"hasta {Limit}");
        return string.Join(" · ", parts);
    }
}
