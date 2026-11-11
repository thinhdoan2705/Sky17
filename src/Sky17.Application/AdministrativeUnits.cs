namespace Sky17.Application;

/// <summary>Province or centrally-run city (2025 two-level administration: province → ward/commune).</summary>
public sealed record ProvinceDto(string Code, string Name, string FullName);

/// <summary>Ward (phường), commune (xã) or special zone (đặc khu).</summary>
public sealed record WardDto(string Code, string Name, string FullName, string ProvinceCode);

/// <summary>
/// Read-only catalog of Vietnamese administrative units. Reference data ships with the app
/// (dataset from github.com/thanglequoc/vietnamese-provinces-database) and is cached in memory.
/// </summary>
public interface IAdministrativeUnits
{
    /// <summary>Dataset version, e.g. "v5.2.0 (388/NQ-UBTVQH16)".</summary>
    string Version { get; }
    IReadOnlyList<ProvinceDto> Provinces { get; }
    IReadOnlyList<WardDto> WardsOf(string? provinceCode);
    ProvinceDto? FindProvince(string? code);
    WardDto? FindWard(string? code);
}

public static class AdministrativeUnitsExtensions
{
    /// <summary>Validates that the ward belongs to the province; returns both or throws a user-facing error.</summary>
    public static (ProvinceDto Province, WardDto Ward) Resolve(this IAdministrativeUnits units, string? provinceCode, string? wardCode)
    {
        var province = units.FindProvince(provinceCode)
            ?? throw new Sky17.Domain.DomainException("Vui lòng chọn tỉnh/thành phố.");
        var ward = units.FindWard(wardCode);
        if (ward is null || ward.ProvinceCode != province.Code)
            throw new Sky17.Domain.DomainException("Vui lòng chọn phường/xã thuộc " + province.FullName + ".");
        return (province, ward);
    }
}
