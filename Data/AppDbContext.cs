using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Contractor> Contractors => Set<Contractor>();

    public DbSet<ContactPhone> ContactPhones => Set<ContactPhone>();

    public DbSet<SmsConsent> SmsConsents => Set<SmsConsent>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<ContactMessage> ContactMessages => Set<ContactMessage>();

    public DbSet<KitchenBooking> KitchenBookings => Set<KitchenBooking>();

    public DbSet<ContractorApplication> ContractorApplications => Set<ContractorApplication>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Customer>(customer =>
        {
            customer.Property(c => c.Name).HasMaxLength(200);
            customer.Property(c => c.Email).HasMaxLength(320);
        });

        builder.Entity<Project>(project =>
        {
            project.Property(p => p.Name).HasMaxLength(200);
            project.Property(p => p.Address).HasMaxLength(500);
            project.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            project.PrimitiveCollection(p => p.Types).ElementType().HasConversion<string>();
            project.HasOne(p => p.Customer).WithMany(c => c.Projects).OnDelete(DeleteBehavior.Restrict);
            project.ToTable(table => table.HasCheckConstraint(
                "CK_Projects_Timeline",
                "\"EndsAt\" IS NULL OR \"StartsAt\" IS NULL OR \"EndsAt\" >= \"StartsAt\""));
        });

        builder.Entity<Contractor>(contractor =>
        {
            contractor.Property(c => c.Name).HasMaxLength(200);
            contractor.Property(c => c.CompanyName).HasMaxLength(200);
            contractor.Property(c => c.Email).HasMaxLength(320);
            contractor.PrimitiveCollection(c => c.Trades).ElementType().HasConversion<string>();
        });

        builder.Entity<ContactPhone>(phone =>
        {
            phone.Property(p => p.Number).HasMaxLength(20);
            phone.HasIndex(p => p.Number);
            // One phone per customer or contractor (unique FK indexes).
            phone.HasOne(p => p.Customer).WithOne(c => c.Phone).HasForeignKey<ContactPhone>(p => p.CustomerId).OnDelete(DeleteBehavior.Cascade);
            phone.HasOne(p => p.Contractor).WithOne(c => c.Phone).HasForeignKey<ContactPhone>(p => p.ContractorId).OnDelete(DeleteBehavior.Cascade);
            phone.ToTable(table => table.HasCheckConstraint(
                "CK_ContactPhones_SingleOwner",
                "(\"CustomerId\" IS NULL) <> (\"ContractorId\" IS NULL)"));
        });

        builder.Entity<SmsConsent>(consent =>
        {
            consent.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            consent.Property(c => c.Source).HasConversion<string>().HasMaxLength(20);
            consent.Property(c => c.PhoneNumber).HasMaxLength(20);
            consent.Property(c => c.Evidence).HasMaxLength(2000);
            consent.Property(c => c.EvidenceVersion).HasMaxLength(40);
            consent.Property(c => c.SourceUrl).HasMaxLength(500);
            consent.Property(c => c.IpAddress).HasMaxLength(45);
            consent.HasIndex(c => new { c.PhoneNumber, c.RecordedAt });
            consent.HasIndex(c => new { c.ContactPhoneId, c.RecordedAt });
            consent.HasOne(c => c.ContactPhone).WithMany(p => p.Consents).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ContractorApplication>(application =>
        {
            application.Property(a => a.Email).HasMaxLength(320);
            application.Property(a => a.FullName).HasMaxLength(200);
            application.Property(a => a.CompanyName).HasMaxLength(200);
            application.PrimitiveCollection(a => a.Professions).ElementType().HasConversion<string>();
            application.Property(a => a.State).HasMaxLength(40);
            application.Property(a => a.City).HasMaxLength(100);
            application.HasIndex(a => a.CreatedAt);
        });

        builder.Entity<KitchenBooking>(booking =>
        {
            booking.Property(b => b.Name).HasMaxLength(200);
            booking.Property(b => b.Email).HasMaxLength(320);
            booking.Property(b => b.Phone).HasMaxLength(20);
            booking.Property(b => b.AppointmentTime).HasMaxLength(5);
            booking.Property(b => b.Notes).HasMaxLength(4000);
            booking.HasIndex(b => b.AppointmentDate);
            booking.HasIndex(b => b.CreatedAt);
        });

        builder.Entity<ContactMessage>(contact =>
        {
            contact.Property(c => c.Name).HasMaxLength(200);
            contact.Property(c => c.Email).HasMaxLength(320);
            contact.Property(c => c.Phone).HasMaxLength(20);
            contact.Property(c => c.Body).HasMaxLength(4000);
            contact.HasIndex(c => c.CreatedAt);
        });

        builder.Entity<Message>(message =>
        {
            message.Property(m => m.Direction).HasConversion<string>().HasMaxLength(20);
            message.Property(m => m.From).HasMaxLength(20);
            message.Property(m => m.To).HasMaxLength(20);
            message.Property(m => m.TwilioSid).HasMaxLength(64);
            message.Property(m => m.Status).HasMaxLength(30);
            message.HasIndex(m => m.TwilioSid).IsUnique();
            message.HasIndex(m => new { m.ContactPhoneId, m.CreatedAt });
            message.HasOne(m => m.ContactPhone).WithMany().OnDelete(DeleteBehavior.SetNull);
            message.HasOne(m => m.Project).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<AppUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(200);
            user.HasOne(u => u.Customer).WithMany().OnDelete(DeleteBehavior.SetNull);
            user.HasOne(u => u.Contractor).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        // Fixed IDs and stamps keep the seed stable, so migrations don't re-insert roles.
        builder.Entity<IdentityRole>().HasData(AppRoles.All.Select(role => new IdentityRole
        {
            Id = $"role-{role.ToLowerInvariant()}",
            Name = role,
            NormalizedName = role.ToUpperInvariant(),
            ConcurrencyStamp = $"role-{role.ToLowerInvariant()}"
        }));
    }
}
