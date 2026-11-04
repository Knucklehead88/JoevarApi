public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string VirtualAssistant = "VA";

    /// <summary>Crew members: staff who only see the customer projects they own, contribute to or are assigned on.</summary>
    public const string Crew = "Crew";

    public const string Contractor = "Contractor";
    public const string Customer = "Customer";

    public static readonly string[] All = [Admin, Manager, VirtualAssistant, Crew, Contractor, Customer];
}
