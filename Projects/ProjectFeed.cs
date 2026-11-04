using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A project's activity feed and chat. Every change and message lands here: it's saved as a
/// <see cref="ProjectActivity"/>, pushed to the project's team (owner, contributors, item assignees) and, when it
/// concerns them, texted: changes to the customer, and messages to whoever is @mentioned (team members, office staff or
/// the customer). Nobody is texted without an opt-in on record (<see cref="SmsSender"/>).
/// </summary>
public sealed partial class ProjectFeed(AppDbContext db, PushQueue push, SmsSender sms)
{
    public static string NameOf(AppUser user) => user.DisplayName ?? user.UserName!;

    /// <summary>"Jane Doe" → "JaneDoe": what follows the @ in a mention.</summary>
    public static string Handle(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The project's team: owner, contributors and the people assigned to its items.</summary>
    public async Task<HashSet<string>> TeamIdsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var owner = await db.Projects.Where(project => project.Id == projectId).Select(project => project.OwnerId).FirstOrDefaultAsync(cancellationToken);
        var contributors = await db.Projects.Where(project => project.Id == projectId)
            .SelectMany(project => project.Contributors).Select(user => user.Id).ToListAsync(cancellationToken);
        var assignees = await db.ProjectItems.Where(item => item.ProjectId == projectId)
            .SelectMany(item => item.Assignees).Select(user => user.Id).ToListAsync(cancellationToken);
        return [.. contributors, .. assignees, .. owner is null ? [] : new[] { owner }];
    }

    /// <summary>
    /// Records a change. <paramref name="customerText"/>, when given, is also texted to the customer (if they agreed
    /// to texts); a failed text is noted on the entry, a customer without phone or consent silently isn't texted.
    /// </summary>
    public async Task<ProjectActivity> RecordChangeAsync(
        Project project,
        AppUser? author,
        string text,
        string? customerText,
        CancellationToken cancellationToken)
    {
        var activity = await AddAsync(project, ProjectActivityKind.Change, author, author is null ? "Joevar" : NameOf(author), false, text, cancellationToken);
        var phone = await CustomerPhoneAsync(project, cancellationToken);
        if (customerText is not null && phone is not null && await sms.IsOptedInAsync(phone, cancellationToken))
        {
            var failure = await sms.SendAsync(phone, $"Joevar update, {project.Name}: {customerText}", project.Id, checkConsent: false, cancellationToken);
            Note(activity, $"{project.Customer.Name} (customer)", failure);
            await db.SaveChangesAsync(cancellationToken);
        }

        await PushAsync(project, activity, author?.Id, [], cancellationToken);
        return activity;
    }

    /// <summary>
    /// A message from a team member, written in the chat or texted in. Everyone @mentioned is texted: team members and
    /// office staff with a phone and consent, and the customer.
    /// </summary>
    public async Task<ProjectActivity> PostMessageAsync(
        Project project,
        AppUser author,
        string text,
        ProjectActivityKind kind,
        CancellationToken cancellationToken)
    {
        var candidates = await MentionCandidatesAsync(project, cancellationToken);
        var mentioned = Mentions(text, candidates);
        var activity = await AddAsync(project, kind, author, NameOf(author), false, text, cancellationToken);

        var mentionedIds = new List<string>();
        foreach (var candidate in mentioned.Where(candidate => candidate.UserId != author.Id))
        {
            if (candidate.UserId is not null)
            {
                mentionedIds.Add(candidate.UserId);
            }

            var label = candidate.IsCustomer ? $"{candidate.Name} (customer)" : candidate.Name;
            if (candidate.Phone is null)
            {
                Note(activity, label, "no phone number");
                continue;
            }

            var body = candidate.IsCustomer
                ? $"{WithoutMentions(text, mentioned.Where(other => other.IsCustomer))}\n– {NameOf(author)}, Joevar"
                : $"Joevar · {project.Name}\n{NameOf(author)}: {text}\nReply to this text to answer in the project chat.";
            Note(activity, label, await sms.SendAsync(candidate.Phone, body, project.Id, checkConsent: true, cancellationToken));
        }

        await db.SaveChangesAsync(cancellationToken);
        await PushAsync(project, activity, author.Id, mentionedIds, cancellationToken);
        return activity;
    }

    /// <summary>
    /// An SMS that reached Joevar's number. A team member's (a staff login with that phone) is posted as their message,
    /// a customer's as a text from the customer, in the project the last text to that number was about (else their
    /// latest project). STOP/START keywords are recorded as consent. Returns false when the number isn't on any project.
    /// </summary>
    public async Task<bool> ReceiveSmsAsync(string from, string body, CancellationToken cancellationToken)
    {
        var keyword = body.Trim().ToUpperInvariant();
        var optOut = keyword is "STOP" or "STOPALL" or "UNSUBSCRIBE" or "CANCEL" or "END" or "QUIT" or "REVOKE" or "OPTOUT";
        var optIn = keyword is "START" or "UNSTOP" or "YES";
        if (optOut || optIn)
        {
            db.SmsConsents.Add(new SmsConsent
            {
                PhoneNumber = from,
                ContactPhoneId = await db.ContactPhones.Where(phone => phone.Number == from).Select(phone => (Guid?)phone.Id).FirstOrDefaultAsync(cancellationToken),
                Status = optOut ? SmsConsentStatus.OptedOut : SmsConsentStatus.OptedIn,
                Source = SmsConsentSource.Keyword,
                Evidence = keyword
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        var lastProjectId = await db.Messages
            .Where(message => message.Direction == MessageDirection.Outbound && message.To == from && message.ProjectId != null)
            .OrderByDescending(message => message.CreatedAt)
            .Select(message => message.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        var staffIds = from userRole in db.UserRoles
                       join role in db.Roles on userRole.RoleId equals role.Id
                       where StaffAccounts.Roles.Contains(role.Name!)
                       select userRole.UserId;
        var member = await db.Users
            .Where(user => user.PhoneNumber == from && staffIds.Contains(user.Id) && user.TwoFactorEnabled
                && (user.LockoutEnd == null || user.LockoutEnd < StaffAccounts.BlockedUntil))
            .FirstOrDefaultAsync(cancellationToken);
        if (member is not null)
        {
            var roles = await db.UserRoles.Where(userRole => userRole.UserId == member.Id)
                .Join(db.Roles, userRole => userRole.RoleId, role => role.Id, (_, role) => role.Name!).ToListAsync(cancellationToken);
            var visible = ProjectAccess.Visible(db, member.Id, roles.Any(StaffAccounts.OfficeRoles.Contains));
            var project = await WithCustomer(visible.Where(candidate => candidate.Id == lastProjectId)).FirstOrDefaultAsync(cancellationToken)
                ?? await WithCustomer(visible.OrderByDescending(candidate => candidate.LastActivityAt)).FirstOrDefaultAsync(cancellationToken);
            if (project is null)
            {
                return false;
            }

            if (optOut || optIn)
            {
                await RecordChangeAsync(project, null, $"{NameOf(member)} {(optOut ? "replied STOP: no more texts to them" : "replied START: texts are back on")}.", null, cancellationToken);
                return true;
            }

            await PostMessageAsync(project, member, body.Trim(), ProjectActivityKind.Sms, cancellationToken);
            return true;
        }

        var customerIds = db.ContactPhones.Where(phone => phone.Number == from && phone.CustomerId != null).Select(phone => phone.CustomerId!.Value);
        var customerProjects = db.Projects.Where(candidate => customerIds.Contains(candidate.CustomerId));
        var customerProject = await WithCustomer(customerProjects.Where(candidate => candidate.Id == lastProjectId)).FirstOrDefaultAsync(cancellationToken)
            ?? await WithCustomer(customerProjects.OrderByDescending(candidate => candidate.LastActivityAt)).FirstOrDefaultAsync(cancellationToken);
        if (customerProject is null)
        {
            return false;
        }

        if (optOut || optIn)
        {
            await RecordChangeAsync(customerProject, null, $"{customerProject.Customer.Name} {(optOut ? "replied STOP: no more texts to them" : "replied START: texts are back on")}.", null, cancellationToken);
            return true;
        }

        var activity = await AddAsync(customerProject, ProjectActivityKind.Sms, null, customerProject.Customer.Name, true, body.Trim(), cancellationToken);
        await PushAsync(customerProject, activity, null, [], cancellationToken);
        return true;
    }

    /// <summary>Who can be @mentioned in a project: its team, the office staff and the customer.</summary>
    public async Task<List<MentionCandidate>> MentionCandidatesAsync(Project project, CancellationToken cancellationToken)
    {
        var team = await TeamIdsAsync(project.Id, cancellationToken);
        var office = from userRole in db.UserRoles
                     join role in db.Roles on userRole.RoleId equals role.Id
                     where StaffAccounts.OfficeRoles.Contains(role.Name!)
                     select userRole.UserId;
        var users = await db.Users
            .Where(user => (team.Contains(user.Id) || office.Contains(user.Id)) && (user.LockoutEnd == null || user.LockoutEnd < StaffAccounts.BlockedUntil))
            .Select(user => new { user.Id, user.UserName, user.DisplayName, user.PhoneNumber })
            .ToListAsync(cancellationToken);
        var candidates = users
            .Select(user => new MentionCandidate(Handle(user.DisplayName ?? user.UserName!), user.DisplayName ?? user.UserName!, user.Id, false, user.PhoneNumber, user.UserName))
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var customerPhone = await CustomerPhoneAsync(project, cancellationToken);
        candidates.Add(new MentionCandidate(Handle(project.Customer.Name), project.Customer.Name, null, true, customerPhone, null));
        return candidates;
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])@([\p{L}\p{N}][\p{L}\p{N}._-]*)")]
    private static partial Regex MentionPattern();

    /// <summary>
    /// The candidates a text mentions: "@JaneDoe" (the whole name), "@Jane" (the first name), the username, or
    /// "@Customer" for the customer. Case doesn't matter.
    /// </summary>
    public static List<MentionCandidate> Mentions(string text, IReadOnlyList<MentionCandidate> candidates)
    {
        var tokens = MentionPattern().Matches(text).Select(match => Handle(match.Groups[1].Value).ToLowerInvariant()).ToHashSet();
        return candidates.Where(candidate => candidate.Matches(tokens)).ToList();
    }

    private static string WithoutMentions(string text, IEnumerable<MentionCandidate> removed)
    {
        var targets = removed.ToList();
        var stripped = MentionPattern().Replace(text, match =>
            targets.Any(candidate => candidate.Matches(new HashSet<string> { Handle(match.Groups[1].Value).ToLowerInvariant() })) ? "" : match.Value);
        return SpacesPattern().Replace(stripped, " ").Trim().TrimStart(',', ':', '-', ' ').Trim();
    }

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpacesPattern();

    private static IQueryable<Project> WithCustomer(IQueryable<Project> projects) =>
        projects.Include(project => project.Customer).ThenInclude(customer => customer.Phone);

    private async Task<string?> CustomerPhoneAsync(Project project, CancellationToken cancellationToken) =>
        project.Customer?.Phone?.Number
        ?? await db.ContactPhones.Where(phone => phone.CustomerId == project.CustomerId).Select(phone => phone.Number).FirstOrDefaultAsync(cancellationToken);

    private async Task<ProjectActivity> AddAsync(
        Project project, ProjectActivityKind kind, AppUser? author, string authorName, bool fromCustomer, string text, CancellationToken cancellationToken)
    {
        var activity = new ProjectActivity
        {
            ProjectId = project.Id,
            Kind = kind,
            AuthorUserId = author?.Id,
            AuthorName = authorName,
            FromCustomer = fromCustomer,
            Text = text.Length > 4000 ? text[..4000] : text
        };
        db.ProjectActivity.Add(activity);
        project.LastActivityAt = activity.CreatedAt;
        await db.SaveChangesAsync(cancellationToken);
        return activity;
    }

    private static void Note(ProjectActivity activity, string who, string? failure)
    {
        if (failure is null)
        {
            activity.Texted.Add(who);
        }
        else
        {
            activity.NotTexted.Add($"{who}: {failure}");
        }
    }

    private async Task PushAsync(Project project, ProjectActivity activity, string? authorId, IEnumerable<string> extra, CancellationToken cancellationToken)
    {
        var recipients = await TeamIdsAsync(project.Id, cancellationToken);
        recipients.UnionWith(extra);
        if (authorId is not null)
        {
            recipients.Remove(authorId);
        }

        if (recipients.Count == 0)
        {
            return;
        }

        var body = activity.Kind == ProjectActivityKind.Change ? activity.Text : $"{activity.AuthorName}: {activity.Text}";
        push.Enqueue(
            new PushAudience.Users(recipients),
            new PushPayload(project.Name, body.Length > 180 ? body[..179] + "…" : body, $"/projects/{project.Id}?chat=1", $"project-{project.Id}"));
    }
}

/// <summary>Someone who can be @mentioned in a project's chat: a staff login (<see cref="UserId"/>) or the customer.</summary>
public sealed record MentionCandidate(string Handle, string Name, string? UserId, bool IsCustomer, string? Phone, string? UserName)
{
    public bool Matches(IReadOnlySet<string> tokens)
    {
        var first = ProjectFeed.Handle(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").ToLowerInvariant();
        return tokens.Contains(Handle.ToLowerInvariant())
            || (first.Length > 0 && tokens.Contains(first))
            || (UserName is not null && tokens.Contains(ProjectFeed.Handle(UserName).ToLowerInvariant()))
            || (IsCustomer && tokens.Contains("customer"));
    }
}
