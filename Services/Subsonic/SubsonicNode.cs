using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Echoplex.Services.Subsonic;

/// <summary>
/// Un elemento de una respuesta Subsonic, que se escribe igual en XML (atributos e hijos) o en JSON (propiedades; los
/// hijos de una lista, siempre como array aunque haya uno solo, como hacen los servidores de referencia).
/// </summary>
public sealed class SubsonicNode
{
    public string Name { get; }
    private readonly List<(string key, object value)> _attrs = new();
    private readonly List<(SubsonicNode node, bool list)> _children = new();
    private readonly HashSet<string> _emptyLists = new();
    /// <summary>Texto del elemento (en JSON sale como "value"): el nombre de un género, una letra…</summary>
    public string? Text { get; set; }

    public SubsonicNode(string name) => Name = name;

    /// <summary>Un atributo; los nulos y las cadenas vacías no se escriben.</summary>
    public SubsonicNode Set(string key, object? value)
    {
        if (value == null || value is string { Length: 0 }) return this;
        int i = _attrs.FindIndex(a => a.key == key);
        if (i >= 0) _attrs[i] = (key, value);
        else _attrs.Add((key, value));
        return this;
    }

    /// <summary>Un hijo único (un objeto en JSON).</summary>
    public SubsonicNode Child(string name)
    {
        var node = new SubsonicNode(name);
        _children.Add((node, false));
        return node;
    }

    public SubsonicNode Add(SubsonicNode node)
    {
        _children.Add((node, false));
        return node;
    }

    /// <summary>Un elemento de una lista (en JSON, todos los de ese nombre van en un array).</summary>
    public SubsonicNode Item(string name)
    {
        var node = new SubsonicNode(name);
        _children.Add((node, true));
        return node;
    }

    public SubsonicNode AddItem(SubsonicNode node)
    {
        _children.Add((node, true));
        return node;
    }

    /// <summary>Que la lista salga en JSON aunque esté vacía ("song": []).</summary>
    public SubsonicNode EmptyList(string name)
    {
        _emptyLists.Add(name);
        return this;
    }

    // ---------- XML ----------

    public void WriteXml(XmlWriter w, string? ns = null)
    {
        if (ns != null) w.WriteStartElement(Name, ns);
        else w.WriteStartElement(Name);
        foreach (var (k, v) in _attrs) w.WriteAttributeString(k, Format(v));
        foreach (var (c, _) in _children) c.WriteXml(w);
        if (Text != null) w.WriteString(Text);
        w.WriteEndElement();
    }

    // ---------- JSON ----------

    public void WriteJsonBody(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        WriteJsonMembers(w);
        w.WriteEndObject();
    }

    public void WriteJsonMembers(Utf8JsonWriter w)
    {
        foreach (var (k, v) in _attrs) WriteValue(w, k, v);
        if (Text != null) w.WriteString("value", Text);
        var written = new HashSet<string>();
        foreach (var (child, list) in _children)
        {
            if (!written.Add(child.Name)) continue;
            if (list)
            {
                w.WriteStartArray(child.Name);
                foreach (var (c, l) in _children)
                    if (l && c.Name == child.Name) c.WriteJsonBody(w);
                w.WriteEndArray();
            }
            else
            {
                w.WritePropertyName(child.Name);
                child.WriteJsonBody(w);
            }
        }
        foreach (var name in _emptyLists)
        {
            if (written.Contains(name)) continue;
            w.WriteStartArray(name);
            w.WriteEndArray();
        }
    }

    private static void WriteValue(Utf8JsonWriter w, string key, object v)
    {
        switch (v)
        {
            case bool b: w.WriteBoolean(key, b); break;
            case int i: w.WriteNumber(key, i); break;
            case long l: w.WriteNumber(key, l); break;
            case uint u: w.WriteNumber(key, u); break;
            case double d: w.WriteNumber(key, Math.Round(d, 2)); break;
            case float f: w.WriteNumber(key, Math.Round(f, 2)); break;
            case DateTime t: w.WriteString(key, IsoDate(t)); break;
            default: w.WriteString(key, v.ToString()); break;
        }
    }

    private static string Format(object v) => v switch
    {
        bool b => b ? "true" : "false",
        DateTime t => IsoDate(t),
        double d => Math.Round(d, 2).ToString(CultureInfo.InvariantCulture),
        float f => Math.Round(f, 2).ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    public static string IsoDate(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>La respuesta completa: &lt;subsonic-response status="ok" …&gt; con lo que haya dentro.</summary>
public static class SubsonicWriter
{
    public const string ApiVersion = "1.16.1";
    private const string Ns = "http://subsonic.org/restapi";

    public static SubsonicNode Ok() => Root("ok");

    public static SubsonicNode Error(int code, string message)
    {
        var root = Root("failed");
        root.Child("error").Set("code", code).Set("message", message);
        return root;
    }

    private static SubsonicNode Root(string status) => new SubsonicNode("subsonic-response")
        .Set("status", status)
        .Set("version", ApiVersion)
        .Set("type", "echoplex")
        .Set("serverVersion", UpdateService.CurrentVersion.ToString())
        .Set("openSubsonic", true);

    /// <summary>Escribe la respuesta: JSON si se pide (f=json) y si no XML, como el Subsonic original.</summary>
    public static (byte[] body, string contentType) Write(SubsonicNode root, string? format, string? callback)
    {
        if (format is "json" or "jsonp")
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WritePropertyName(root.Name);
                root.WriteJsonBody(w);
                w.WriteEndObject();
            }
            var json = ms.ToArray();
            if (format == "jsonp" && !string.IsNullOrEmpty(callback))
                return (Encoding.UTF8.GetBytes($"{callback}({Encoding.UTF8.GetString(json)});"), "text/javascript; charset=utf-8");
            return (json, "application/json; charset=utf-8");
        }
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false };
        using var xs = new MemoryStream();
        using (var w = XmlWriter.Create(xs, settings))
        {
            w.WriteStartDocument();
            root.WriteXml(w, Ns);
            w.WriteEndDocument();
        }
        return (xs.ToArray(), "text/xml; charset=utf-8");
    }
}
