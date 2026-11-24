using System.Text.Json;

namespace BertsIptvHub;

/// <summary>Favourites shared by every TV, kept in data/favorites.json.</summary>
public sealed class FavoritesStore
{
    public sealed class Data
    {
        public List<int> Live { get; set; } = new();
        public List<int> Vod { get; set; } = new();
        public List<int> Series { get; set; } = new();

        public Data Clone() => new() { Live = Live.ToList(), Vod = Vod.ToList(), Series = Series.ToList() };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private readonly ILogger<FavoritesStore> _log;
    private Data _data = new();

    public event Action<Data>? Changed;

    public FavoritesStore(HubOptions o, ILogger<FavoritesStore> log)
    {
        _log = log;
        _path = Path.Combine(o.DataDirFull, "favorites.json");
        try
        {
            if (File.Exists(_path))
                _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), Json) ?? new Data();
        }
        catch (Exception ex)
        {
            _log.LogError("Could not read favorites.json ({Msg}); starting empty", ex.Message);
        }
    }

    public Data Snapshot()
    {
        lock (_lock) return _data.Clone();
    }

    public static bool IsKind(string kind) => ListFor(new Data(), kind) != null;

    public bool Replace(string kind, IEnumerable<int> ids) =>
        Mutate(kind, list => { list.Clear(); list.AddRange(ids.Distinct()); });

    public bool Add(string kind, int id) =>
        Mutate(kind, list => { if (!list.Contains(id)) list.Add(id); });

    public bool Remove(string kind, int id) =>
        Mutate(kind, list => list.Remove(id));

    private bool Mutate(string kind, Action<List<int>> change)
    {
        Data copy;
        lock (_lock)
        {
            var list = ListFor(_data, kind);
            if (list is null) return false;
            change(list);
            Save();
            copy = _data.Clone();
        }
        Changed?.Invoke(copy);
        return true;
    }

    private static List<int>? ListFor(Data d, string kind) => kind.ToLowerInvariant() switch
    {
        "live" => d.Live,
        "vod" or "movie" or "movies" => d.Vod,
        "series" => d.Series,
        _ => null,
    };

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogError("Could not save favorites: {Msg}", ex.Message);
        }
    }
}
