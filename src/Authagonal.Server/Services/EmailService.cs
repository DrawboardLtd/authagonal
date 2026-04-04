using Authagonal.Core.Services;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Authagonal.Server.Services;

public sealed class EmailService(IConfiguration configuration, ILogger<EmailService> logger) : IEmailService
{
    private const string ConfigSection = "Email";

    public async Task SendVerificationEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
    {
        if (IsTestEmail(email))
        {
            logger.LogInformation("Skipping verification email for test address: {Email}", email);
            return;
        }

        var templateId = configuration[$"{ConfigSection}:VerificationTemplateId"]
            ?? throw new InvalidOperationException("VerificationTemplateId is not configured");

        await SendTemplateEmailAsync(email, templateId, new { callbackUrl }, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
    {
        if (IsTestEmail(email))
        {
            logger.LogInformation("Skipping password reset email for test address: {Email}", email);
            return;
        }

        var templateId = configuration[$"{ConfigSection}:PasswordResetTemplateId"]
            ?? throw new InvalidOperationException("PasswordResetTemplateId is not configured");

        await SendTemplateEmailAsync(email, templateId, new { callbackUrl }, ct);
    }

    private async Task SendTemplateEmailAsync(string toEmail, string templateId, object dynamicData, CancellationToken ct)
    {
        var apiKey = configuration[$"{ConfigSection}:SendGridApiKey"]
            ?? throw new InvalidOperationException("SendGridApiKey is not configured");

        var senderEmail = configuration[$"{ConfigSection}:SenderEmail"]
            ?? throw new InvalidOperationException("SenderEmail is not configured");

        var senderName = configuration[$"{ConfigSection}:SenderName"] ?? "Authagonal";

        var client = new SendGridClient(apiKey);

        var msg = new SendGridMessage
        {
            From = new EmailAddress(senderEmail, senderName),
            TemplateId = templateId
        };

        msg.AddTo(new EmailAddress(toEmail));
        msg.SetTemplateData(dynamicData);

        var response = await client.SendEmailAsync(msg, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Body.ReadAsStringAsync(ct);
            logger.LogError(
                "SendGrid returned {StatusCode} when sending template {TemplateId} to {Email}: {Body}",
                response.StatusCode, templateId, toEmail, body);

            throw new InvalidOperationException($"Failed to send email via SendGrid: {response.StatusCode}");
        }

        logger.LogInformation("Email sent successfully using template {TemplateId} to {Email}", templateId, toEmail);
    }

    private static bool IsTestEmail(string email)
    {
        return email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase);
    }
}
