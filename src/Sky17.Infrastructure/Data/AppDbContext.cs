using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Sky17.Domain;

namespace Sky17.Infrastructure.Data;

public class ApplicationUser : IdentityUser
{
    /// <summary>Deactivated accounts cannot sign in (also locked out + security stamp rotated).</summary>
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<StoreMember> StoreMembers => Set<StoreMember>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderStatusHistory> OrderStatusHistories => Set<OrderStatusHistory>();
    public DbSet<DriverProfile> DriverProfiles => Set<DriverProfile>();
    public DbSet<DeliveryAssignment> DeliveryAssignments => Set<DeliveryAssignment>();
    public DbSet<DeliveryRateRule> DeliveryRateRules => Set<DeliveryRateRule>();
    public DbSet<DispatchOffer> DispatchOffers => Set<DispatchOffer>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CodTransaction> CodTransactions => Set<CodTransaction>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<SettlementItem> SettlementItems => Set<SettlementItem>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.IsActive).HasDefaultValue(true);
        });

        b.Entity<UserProfile>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.Property(x => x.FullName).HasMaxLength(100);
            e.Property(x => x.PhoneNumber).HasMaxLength(20);
            e.HasOne<ApplicationUser>().WithOne().HasForeignKey<UserProfile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Address>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Label).HasMaxLength(40);
            e.Property(x => x.RecipientName).HasMaxLength(100);
            e.Property(x => x.PhoneNumber).HasMaxLength(20);
            e.Property(x => x.Street).HasMaxLength(200);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.Ward).HasMaxLength(100);
            e.Property(x => x.WardCode).HasMaxLength(10);
            e.Property(x => x.ProvinceCode).HasMaxLength(10);
            e.Property(x => x.Note).HasMaxLength(200);
            e.Ignore(x => x.FullText);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Store>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.PhoneNumber).HasMaxLength(20);
            e.Property(x => x.Address).HasMaxLength(200);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Name);
        });

        b.Entity<StoreMember>(e =>
        {
            e.HasIndex(x => new { x.StoreId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Store).WithMany(s => s.Members).HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Category>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasIndex(x => new { x.StoreId, x.Name }).IsUnique();
            e.HasOne(x => x.Store).WithMany(s => s.Categories).HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Product>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.StoreId, x.IsActive });
            e.HasIndex(x => x.Name);
            e.HasOne(x => x.Store).WithMany(s => s.Products).HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Product_Price", "\"Price\" > 0");
                t.HasCheckConstraint("CK_Product_Stock", "\"Stock\" >= 0");
            });
        });

        b.Entity<ProductImage>(e =>
        {
            e.Property(x => x.Url).HasMaxLength(300);
            e.HasOne(x => x.Product).WithMany(p => p.Images).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Cart>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<CartItem>(e =>
        {
            e.HasIndex(x => new { x.CartId, x.ProductId }).IsUnique();
            e.HasOne(x => x.Cart).WithMany(c => c.Items).HasForeignKey(x => x.CartId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_CartItem_Quantity", "\"Quantity\" > 0"));
        });

        b.Entity<Order>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(24);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.StoreId, x.Status });
            e.HasIndex(x => new { x.DriverId, x.Status });
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.PlacedAtUtc);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.IdempotencyKey).HasMaxLength(64);
            e.Property(x => x.RecipientName).HasMaxLength(100);
            e.Property(x => x.RecipientPhone).HasMaxLength(20);
            e.Property(x => x.DeliveryAddress).HasMaxLength(300);
            e.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Order_GrandTotal", "\"GrandTotal\" >= 0"));
        });

        b.Entity<OrderItem>(e =>
        {
            e.HasOne(x => x.Order).WithMany(o => o.Items).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ProductName).HasMaxLength(120);
        });

        b.Entity<OrderStatusHistory>(e =>
        {
            e.HasIndex(x => new { x.OrderId, x.ChangedAtUtc });
            e.HasOne(x => x.Order).WithMany(o => o.History).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Reason).HasMaxLength(500);
        });

        b.Entity<DriverProfile>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => new { x.Status, x.IsOnline });
            e.Property(x => x.LicensePlate).HasMaxLength(20);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<ApplicationUser>().WithOne().HasForeignKey<DriverProfile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeliveryAssignment>(e =>
        {
            // At most ONE active assignment per order: DB-level guard against double assignment.
            e.HasIndex(x => x.OrderId).IsUnique().HasFilter("\"IsActive\" = 1").HasDatabaseName("IX_DeliveryAssignments_OrderId_Active");
            e.HasIndex(x => new { x.DriverId, x.IsActive });
            e.HasOne(x => x.Order).WithMany(o => o.Assignments).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DispatchOffer>(e =>
        {
            // One pending offer per order and one ringing popup per driver, enforced by the database.
            e.HasIndex(x => x.OrderId).IsUnique().HasFilter("\"Status\" = 0").HasDatabaseName("IX_DispatchOffers_OrderId_Pending");
            e.HasIndex(x => x.DriverId).IsUnique().HasFilter("\"Status\" = 0").HasDatabaseName("IX_DispatchOffers_DriverId_Pending");
            e.HasIndex(x => new { x.OrderId, x.DriverId });
            e.HasIndex(x => new { x.Status, x.ExpiresAtUtc });
            e.HasOne(x => x.Order).WithMany(o => o.Offers).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Payment>(e =>
        {
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasOne(x => x.Order).WithOne(o => o.Payment).HasForeignKey<Payment>(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CodTransaction>(e =>
        {
            e.HasIndex(x => new { x.DriverId, x.CreatedAtUtc });
            // An order's COD can only be recorded as collected once.
            e.HasIndex(x => new { x.OrderId, x.Type }).IsUnique();
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Settlement>().WithMany().HasForeignKey(x => x.SettlementId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Settlement>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => new { x.Type, x.Status });
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SettlementItem>(e =>
        {
            // Idempotency: an order can be in at most one (non-cancelled) settlement per party type.
            e.HasIndex(x => new { x.OrderId, x.Type }).IsUnique();
            e.HasOne(x => x.Settlement).WithMany(s => s.Items).HasForeignKey(x => x.SettlementId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAtUtc });
            e.Property(x => x.Title).HasMaxLength(150);
            e.Property(x => x.Message).HasMaxLength(500);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.Action).HasMaxLength(80);
            e.Property(x => x.EntityType).HasMaxLength(60);
            e.Property(x => x.Details).HasMaxLength(1000);
        });

        b.Entity<SystemSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(80);
            e.Property(x => x.Value).HasMaxLength(500);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampEntities();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampEntities();
        return base.SaveChanges();
    }

    private void StampEntities()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is IAuditable a)
            {
                if (entry.State == EntityState.Added && a.CreatedAtUtc == default) a.CreatedAtUtc = now;
                if (entry.State == EntityState.Modified) a.UpdatedAtUtc = now;
            }
            if (entry.Entity is IVersioned v && entry.State == EntityState.Modified)
            {
                v.Version++;
            }
            if (entry.Entity is ApplicationUser u && entry.State == EntityState.Added && u.CreatedAtUtc == default)
            {
                u.CreatedAtUtc = now;
            }
        }
    }
}
