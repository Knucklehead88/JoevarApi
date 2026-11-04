public static class AuthPolicies
{
    /// <summary>Team members who manage customers, projects and contractors.</summary>
    public const string Staff = "Staff";

    /// <summary>Admins (with the authenticator on), who also manage the staff logins.</summary>
    public const string Admin = "Admin";

    /// <summary>Everyone who works on customer projects: the office and Crew (with the authenticator on).</summary>
    public const string ProjectTeam = "ProjectTeam";
}
