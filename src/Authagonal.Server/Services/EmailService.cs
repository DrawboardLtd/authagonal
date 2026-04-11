using System.Net.Http.Json;
using System.Text.Json;
using Authagonal.Core.Services;

namespace Authagonal.Server.Services;

public sealed class EmailService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<EmailService> logger) : IEmailService
{
    private const string ConfigSection = "Email";
    private const string ResendApiUrl = "https://api.resend.com/emails";

    public async Task SendVerificationEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
    {
        if (IsTestEmail(email))
        {
            logger.LogInformation("Skipping verification email for test address: {Email}", email);
            return;
        }

        var subject = "Verify your email address";
        var html = $"""
            <div style="font-family: sans-serif; max-width: 480px; margin: 0 auto;">
                <h2>Verify your email</h2>
                <p>Click the link below to verify your email address:</p>
                <p><a href="{callbackUrl}" style="display: inline-block; padding: 12px 24px; background: #2563eb; color: white; text-decoration: none; border-radius: 6px;">Verify Email</a></p>
                <p style="color: #6b7280; font-size: 14px; margin-top: 24px;">If you didn't create an account, you can safely ignore this email.</p>
            </div>
            """;

        await SendAsync(email, subject, html, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl, CancellationToken ct = default)
    {
        if (IsTestEmail(email))
        {
            logger.LogInformation("Skipping password reset email for test address: {Email}", email);
            return;
        }

        var subject = "Reset your password";
        var html = $"""
            <div style="font-family: sans-serif; max-width: 480px; margin: 0 auto;">
                <h2>Reset your password</h2>
                <p>Click the link below to set a new password:</p>
                <p><a href="{callbackUrl}" style="display: inline-block; padding: 12px 24px; background: #2563eb; color: white; text-decoration: none; border-radius: 6px;">Reset Password</a></p>
                <p style="color: #6b7280; font-size: 14px; margin-top: 24px;">This link expires in 1 hour. If you didn't request a password reset, you can safely ignore this email.</p>
            </div>
            """;

        await SendAsync(email, subject, html, ct);
    }

    private async Task SendAsync(string toEmail, string subject, string html, CancellationToken ct)
    {
        var apiKey = configuration[$"{ConfigSection}:ResendApiKey"]
            ?? throw new InvalidOperationException("Email:ResendApiKey is not configured");

        var senderEmail = configuration[$"{ConfigSection}:SenderEmail"]
            ?? throw new InvalidOperationException("Email:SenderEmail is not configured");

        var senderName = configuration[$"{ConfigSection}:SenderName"] ?? "Authagonal";

        var client = httpClientFactory.CreateClient("Resend");

        using var request = new HttpRequestMessage(HttpMethod.Post, ResendApiUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            from = $"{senderName} <{senderEmail}>",
            to = new[] { toEmail },
            subject,
            html
        });

        var response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Resend returned {StatusCode} when sending to {Email}: {Body}",
                response.StatusCode, toEmail, body);
            throw new InvalidOperationException($"Failed to send email via Resend: {response.StatusCode}");
        }

        logger.LogInformation("Email sent to {Email} via Resend (subject: {Subject})", toEmail, subject);
    }

    private static bool IsTestEmail(string email)
    {
        return email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase);
    }
}
