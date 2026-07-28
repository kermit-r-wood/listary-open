using System.IO;
using System.Text;
using OutlookAttachment = MsgReader.Outlook.Storage.Attachment;
using OutlookMessage = MsgReader.Outlook.Storage.Message;

namespace ListaryOpen.App.Previewing;

internal sealed class OutlookMessagePreviewProvider : IFilePreviewProvider
{
    private const long MaximumFileBytes = 32L * 1024 * 1024;
    private const int MaximumBodyCharacters = 256 * 1024;
    private const int MaximumAttachments = 100;
    private const int MaximumRecipients = 200;
    private const int MaximumOutputCharacters = 384 * 1024;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.OutlookMessage);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.SizeBytes <= 0 || context.SizeBytes > MaximumFileBytes)
        {
            return PreviewContent.ForText(
                "Outlook message metadata",
                $"Outlook message{Environment.NewLine}{Environment.NewLine}" +
                $"Size: {FilePreviewPane.FormatSize(context.SizeBytes)}{Environment.NewLine}" +
                $"Content extraction is limited to {FilePreviewPane.FormatSize(MaximumFileBytes)} files.");
        }

        using var message = new OutlookMessage(context.FullPath, FileAccess.Read);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new StringBuilder("Outlook message")
            .AppendLine().AppendLine();
        Append(builder, "From", FormatAddress(message.Sender?.DisplayName, message.Sender?.Email));
        Append(builder, "Subject", message.Subject);
        if (message.SentOn is { } sent)
        {
            Append(builder, "Sent", sent.ToString("u"));
        }

        var recipients = message.Recipients?.Take(MaximumRecipients).ToArray() ?? [];
        foreach (var group in recipients.GroupBy(recipient => recipient.Type))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = group.Select(recipient =>
                    FormatAddress(recipient.DisplayName, recipient.Email))
                .Where(value => !string.IsNullOrWhiteSpace(value));
            Append(builder, group.Key?.ToString() ?? "Recipient", string.Join("; ", values));
        }
        if ((message.Recipients?.Count ?? 0) > MaximumRecipients)
        {
            builder.AppendLine("… additional recipients omitted");
        }

        builder.AppendLine();
        var body = message.BodyText;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(message.BodyHtml))
        {
            body = TextPreviewProvider.ConvertHtmlToText(message.BodyHtml);
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            builder.AppendLine("(No displayable plain-text or HTML body)");
        }
        else
        {
            builder.AppendLine(body.Length <= MaximumBodyCharacters
                ? body.Trim()
                : body[..MaximumBodyCharacters].Trim() + Environment.NewLine + "… body truncated");
        }

        var attachments = message.Attachments?.Take(MaximumAttachments).ToArray() ?? [];
        if (attachments.Length > 0)
        {
            builder.AppendLine().Append("Attachments (")
                .Append(message.Attachments!.Count).AppendLine("):");
            foreach (var item in attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (item)
                {
                    case OutlookAttachment attachment:
                        builder.Append("• ")
                            .AppendLine(BoundField(attachment.FileName ?? "(unnamed attachment)"));
                        break;
                    case OutlookMessage embedded:
                        builder.Append("• Embedded message: ")
                            .AppendLine(BoundField(embedded.Subject ?? "(no subject)"));
                        break;
                    default:
                        builder.AppendLine("• (unsupported attachment object)");
                        break;
                }
            }
            if (message.Attachments.Count > MaximumAttachments)
            {
                builder.AppendLine("… additional attachments omitted");
            }
        }
        builder.AppendLine()
            .AppendLine("Attachments are listed but never saved, opened, or rendered.");
        if (builder.Length > MaximumOutputCharacters)
        {
            builder.Length = MaximumOutputCharacters;
            builder.AppendLine().AppendLine("… preview truncated");
        }
        return PreviewContent.ForText("Safe Outlook message", builder.ToString());
    }

    private static void Append(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(label).Append(": ").AppendLine(BoundField(value));
        }
    }

    private static string? FormatAddress(string? displayName, string? email)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return email;
        }
        return string.IsNullOrWhiteSpace(email) ||
               displayName.Contains(email, StringComparison.OrdinalIgnoreCase)
            ? displayName
            : $"{displayName} <{email}>";
    }

    private static string BoundField(string value)
    {
        var normalized = value.Replace('\0', ' ').ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 4096 ? normalized : normalized[..4096] + "…";
    }
}
