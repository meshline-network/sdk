using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;

namespace Meshline.Examples;

public static class Messaging
{
    #region profile
    public static Task<AccountProfile> UpdateProfileAsync(
        MeshlineClient client, CancellationToken cancellationToken = default) =>
        client.ProfileManager.UpdateProfileAsync(new ProfileUpdate
        {
            Nickname = "Alice",
            Bio = FieldUpdate<string>.Delete
            // Omitted Avatar and PublicDiscovery fields remain unchanged.
        }, cancellationToken);
    #endregion

    #region contact-request
    public static Task<ContactRequestInfo> RequestContactAsync(
        MeshlineClient client, string accountId, CancellationToken cancellationToken = default) =>
        client.MessageManager.AddContactAsync(accountId, "Hello from my app", cancellationToken);
    #endregion

    #region accept-contact
    public static Task<ContactInfo> AcceptContactAsync(
        MeshlineClient receivingClient, string requesterAccountId,
        CancellationToken cancellationToken = default) =>
        receivingClient.MessageManager.AcceptContactRequestAsync(requesterAccountId, cancellationToken);
    #endregion

    #region direct-message
    public static async Task<MessageSendStatus> SendTextAsync(
        MeshlineClient client, string authorizedContactAccountId, string text,
        CancellationToken cancellationToken = default)
    {
        var status = await client.MessageManager.SendMessageAsync(
            authorizedContactAccountId,
            new DirectMessageDraft
            {
                Body = new MessageBody { ContentType = "text/plain", Text = text }
            }, cancellationToken);
        Console.WriteLine($"{status.MessageId}: {status.State}");
        return status;
    }
    #endregion

    #region outbox
    public static async Task PrintPendingMessagesAsync(
        MeshlineClient client, CancellationToken cancellationToken = default)
    {
        await using var reader = await client.MessageManager.GetOutboxAsync(
            state: MessageSendState.Queued | MessageSendState.Submitting | MessageSendState.SubmissionUnknown,
            cancellationToken: cancellationToken);
        while (true)
        {
            var batch = await reader.ReadNextAsync(50, cancellationToken);
            if (batch.Count == 0) break;
            foreach (var message in batch)
                Console.WriteLine($"{message.MessageId}: {message.State}");
        }
    }
    #endregion

    #region conversations
    public static async Task PrintUnreadConversationsAsync(
        MeshlineClient client, CancellationToken cancellationToken = default)
    {
        await using var reader = await client.GetConversationsAsync(
            new ConversationQuery { UnreadOnly = true }, cancellationToken);
        while (true)
        {
            var batch = await reader.ReadNextAsync(50, cancellationToken);
            if (batch.Count == 0) break;
            foreach (var conversation in batch)
                Console.WriteLine($"{conversation.ConversationId}: {conversation.UnreadCount}");
        }
    }

    // Call only when the application considers the conversation read.
    public static Task MarkConversationReadAsync(
        MeshlineClient client, string conversationId, CancellationToken cancellationToken = default) =>
        client.MarkReadAsync(conversationId, cancellationToken);
    #endregion

    #region history
    public static async Task PrintLocalHistoryAsync(
        MeshlineClient client, string peerAccountId, CancellationToken cancellationToken = default)
    {
        await using var reader = await client.MessageManager.GetMessageHistoryAsync(
            peerAccountId, cancellationToken);
        while (true)
        {
            var batch = await reader.ReadNextAsync(50, cancellationToken);
            if (batch.Count == 0) break;
            foreach (var message in batch)
                Console.WriteLine($"{message.Key.Sender}: {message.Body?.Text}");
        }
    }
    #endregion
}
