using Cortex.Application.Commerce;
using Cortex.Application.Notifications;

namespace Casewell.Host;

/// <summary>
/// When a firm's workspace is provisioned — by an operator or a completed checkout — the new
/// administrator gets their welcome email. Skips silently until email delivery is configured
/// (the "Email" section); the platform guarantees a hook failure never rolls back the tenant.
/// </summary>
public sealed class WelcomeEmailHook(ISmtpTransport smtp) : ITenantProvisionedHook
{
    public async Task OnTenantProvisionedAsync(TenantProvisionedContext context, CancellationToken cancellationToken = default)
    {
        if (!smtp.IsConfigured)
        {
            return;
        }

        await smtp.SendAsync(new EmailMessage(
            To: context.AdminEmail,
            Subject: $"Your practice workspace '{context.Name}' is ready",
            TextBody:
                $"Welcome to Casewell!\n\n" +
                $"Your workspace '{context.Name}' ({context.Slug}) is live and you are its administrator " +
                $"({context.AdminEmail}).\n\n" +
                "First steps:\n" +
                "  1. Sign in and open your first matter — the assistant runs the conflict check first.\n" +
                "  2. Docket a deadline: just mention the date in chat.\n" +
                (context.MaxSeats is { } seats and > 1
                    ? $"  3. Invite your team under Admin → Users ({seats} seats included).\n"
                    : "") +
                "\nEvery AI action is permission-checked, approval-gated, and audited — you stay in charge.\n" +
                "AI output is a starting point for attorney review, never legal advice."),
            cancellationToken);
    }
}
