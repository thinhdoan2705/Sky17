using System.Text.Json;
using Sky17.Application;

namespace Sky17.Infrastructure.Services;

/// <summary>Loads the embedded administrative-unit dataset (Data/Reference/vn-units.json) once.</summary>
public sealed class AdministrativeUnitCatalog : IAdministrativeUnits
{
    private readonly Dictionary<string, ProvinceDto> provinces;
    private readonly Dictionary<string, WardDto> wards;
    private readonly Dictionary<string, IReadOnlyList<WardDto>> wardsByProvince;

    public AdministrativeUnitCatalog()
    {
        using var stream = typeof(AdministrativeUnitCatalog).Assembly.GetManifestResourceStream("Sky17.Infrastructure.Data.Reference.vn-units.json")
            ?? throw new InvalidOperationException("Embedded resource vn-units.json not found.");
        var data = JsonSerializer.Deserialize<Dataset>(stream) ?? throw new InvalidOperationException("vn-units.json is empty.");
        var culture = System.Globalization.CultureInfo.GetCultureInfo("vi-VN");
        var cmp = StringComparer.Create(culture, ignoreCase: true);

        Provinces = data.provinces.Select(p => new ProvinceDto(p.c, p.n, p.f)).OrderBy(p => p.Name, cmp).ToList();
        provinces = Provinces.ToDictionary(p => p.Code);
        var allWards = data.wards.Select(w => new WardDto(w.c, w.n, w.f, w.p)).ToList();
        wards = allWards.ToDictionary(w => w.Code);
        wardsByProvince = allWards.GroupBy(w => w.ProvinceCode)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<WardDto>)g.OrderBy(w => w.Name, cmp).ToList());
        Version = $"{data.version} ({data.decree})";
    }

    public string Version { get; }
    public IReadOnlyList<ProvinceDto> Provinces { get; }

    public IReadOnlyList<WardDto> WardsOf(string? provinceCode) =>
        provinceCode is not null && wardsByProvince.TryGetValue(provinceCode, out var list) ? list : [];

    public ProvinceDto? FindProvince(string? code) => code is not null && provinces.TryGetValue(code, out var p) ? p : null;
    public WardDto? FindWard(string? code) => code is not null && wards.TryGetValue(code, out var w) ? w : null;

    // Compact JSON shape produced from the dataset's SQL script.
    private sealed record Dataset(string version, string decree, List<P> provinces, List<W> wards);
    private sealed record P(string c, string n, string f, int u);
    private sealed record W(string c, string n, string f, string p, int u);
}
