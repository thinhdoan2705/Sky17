using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Infrastructure.Services;

/// <summary>
/// Registration with role-specific side effects. Uses the scoped AppDbContext shared with UserManager
/// so that the user and their profile/store/driver records are created in one transaction.
/// </summary>
public sealed class AccountService(UserManager<ApplicationUser> userManager, AppDbContext db, Notifier notifier) : IAccountService
{
    public async Task<RegisterResult> RegisterAsync(RegisterInput input, CancellationToken ct = default)
    {
        var errors = new List<string>();
        try { InputValidation.Validate(input); }
        catch (DomainException ex) { return new RegisterResult(false, null, [ex.Message]); }

        if (input.Kind == RegistrationKind.Seller && (string.IsNullOrWhiteSpace(input.StoreName) || string.IsNullOrWhiteSpace(input.StoreAddress)))
            errors.Add("Vui lòng nhập tên và địa chỉ cửa hàng.");
        if (input.Kind == RegistrationKind.Driver && string.IsNullOrWhiteSpace(input.LicensePlate))
            errors.Add("Vui lòng nhập biển số xe.");
        if (errors.Count > 0) return new RegisterResult(false, null, errors);

        var email = input.Email.Trim();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            PhoneNumber = input.PhoneNumber.Trim(),
            // No e-mail provider is configured in the Lite MVP; see DECISIONS.md.
            EmailConfirmed = true,
            IsActive = true,
            LockoutEnabled = true,
        };
        var created = await userManager.CreateAsync(user, input.Password);
        if (!created.Succeeded) return new RegisterResult(false, null, created.Errors.Select(Translate).ToList());

        var role = input.Kind switch
        {
            RegistrationKind.Seller => Roles.Seller,
            RegistrationKind.Driver => Roles.Driver,
            _ => Roles.Customer,
        };
        // Public registration can never grant Admin.
        var roleResult = await userManager.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded) return new RegisterResult(false, null, roleResult.Errors.Select(Translate).ToList());

        db.UserProfiles.Add(new UserProfile { UserId = user.Id, FullName = input.FullName.Trim(), PhoneNumber = input.PhoneNumber.Trim() });

        string? adminMessage = null;
        if (input.Kind == RegistrationKind.Seller)
        {
            var store = new Store
            {
                Name = input.StoreName!.Trim(),
                Address = input.StoreAddress!.Trim(),
                Tagline = input.StoreTagline.Clean(),
                PhoneNumber = input.PhoneNumber.Trim(),
                Status = StoreStatus.PendingApproval,
            };
            store.Members.Add(new StoreMember { UserId = user.Id, Role = StoreMemberRole.Owner });
            db.Stores.Add(store);
            adminMessage = $"Cửa hàng “{store.Name}” chờ phê duyệt.";
        }
        else if (input.Kind == RegistrationKind.Driver)
        {
            db.DriverProfiles.Add(new DriverProfile
            {
                UserId = user.Id,
                LicensePlate = input.LicensePlate!.Trim().ToUpperInvariant(),
                VehicleType = input.VehicleType.Clean() ?? "Xe máy",
                IdentityNumber = input.IdentityNumber.Clean(),
                Status = DriverStatus.PendingApproval,
            });
            adminMessage = $"Tài xế {input.FullName.Trim()} chờ phê duyệt.";
        }
        db.Audit(user.Id, $"Account.Registered.{role}", "User", user.Id, email);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (adminMessage is not null)
        {
            var admins = await Notifier.AdminIdsAsync(db, ct);
            await notifier.NotifyAsync(admins, "approval", "Hồ sơ mới cần duyệt", adminMessage,
                input.Kind == RegistrationKind.Seller ? "/admin/stores" : "/admin/drivers", null, ct);
        }
        return new RegisterResult(true, user.Id, []);
    }

    public async Task<string> GetHomeUrlAsync(string userId, CancellationToken ct = default)
    {
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == userId select r.Name).ToListAsync(ct);
        if (roles.Contains(Roles.Admin)) return "/admin";
        if (roles.Contains(Roles.Seller)) return "/seller";
        if (roles.Contains(Roles.Driver)) return "/driver";
        return "/";
    }

    private static string Translate(IdentityError e) => e.Code switch
    {
        "DuplicateUserName" or "DuplicateEmail" => "Email này đã được đăng ký.",
        "PasswordTooShort" => "Mật khẩu quá ngắn.",
        "PasswordRequiresDigit" => "Mật khẩu cần ít nhất một chữ số.",
        "PasswordRequiresLower" => "Mật khẩu cần ít nhất một chữ thường.",
        "PasswordRequiresUpper" => "Mật khẩu cần ít nhất một chữ hoa.",
        "PasswordRequiresNonAlphanumeric" => "Mật khẩu cần ít nhất một ký tự đặc biệt.",
        "InvalidEmail" => "Email không hợp lệ.",
        _ => e.Description,
    };
}

/// <summary>Stores validated images under wwwroot/uploads. Only JPEG/PNG/WebP up to 2 MB, verified by magic bytes.</summary>
public sealed class LocalFileStorage(string webRootPath) : IFileStorage
{
    public const long MaxBytes = 2 * 1024 * 1024;

    private static readonly Dictionary<string, (string Ext, byte[][] Magic)> Allowed = new()
    {
        ["image/jpeg"] = (".jpg", [[0xFF, 0xD8, 0xFF]]),
        ["image/png"] = (".png", [[0x89, 0x50, 0x4E, 0x47]]),
        ["image/webp"] = (".webp", [[0x52, 0x49, 0x46, 0x46]]),
    };

    public async Task<string> SaveImageAsync(Stream content, string fileName, string contentType, string folder, CancellationToken ct = default)
    {
        if (!Allowed.TryGetValue(contentType.ToLowerInvariant(), out var spec))
            throw new DomainException("Chỉ chấp nhận ảnh JPG, PNG hoặc WebP.");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) throw new DomainException("Định dạng tệp không hợp lệ.");

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw new DomainException("Ảnh tối đa 2MB.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        if (!spec.Magic.Any(m => bytes.Length >= m.Length && bytes.AsSpan(0, m.Length).SequenceEqual(m)))
            throw new DomainException("Nội dung tệp không phải ảnh hợp lệ.");
        if (contentType == "image/webp" && (bytes.Length < 12 || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)))
            throw new DomainException("Nội dung tệp không phải ảnh hợp lệ.");

        var safeFolder = string.Join('/', folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => new string(s.Where(char.IsLetterOrDigit).ToArray())));
        var dir = Path.Combine(webRootPath, "uploads", safeFolder);
        Directory.CreateDirectory(dir);
        var name = $"{Guid.NewGuid():N}{spec.Ext}"; // never trust the client file name
        await File.WriteAllBytesAsync(Path.Combine(dir, name), bytes, ct);
        return $"/uploads/{safeFolder}/{name}";
    }

    public Task DeleteAsync(string url, CancellationToken ct = default)
    {
        if (!url.StartsWith("/uploads/", StringComparison.Ordinal)) return Task.CompletedTask;
        var root = Path.GetFullPath(Path.Combine(webRootPath, "uploads"));
        var full = Path.GetFullPath(Path.Combine(webRootPath, url.TrimStart('/')));
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }
}
