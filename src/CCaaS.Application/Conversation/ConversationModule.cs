using CCaaS.Application.Common;
using CCaaS.Domain.Conversation;

namespace CCaaS.Application.Conversation;

public record PostMessageRequest(Guid ConversationId, string Direction, string Body, string? SenderAgentId, string? ProviderMessageId);
public record UnifiedInboxItemDto(Guid ConversationId, ConversationChannelType ChannelType, ConversationStatus Status, Guid? AssignedAgentId, DateTime UpdatedAt);

public interface IConversationService
{
    /// <summary>
    /// Central "get-or-create" used by both the inbound message flow (Section 9) and the
    /// inbound call flow (Section 8) so a customer's voice and WhatsApp history land in the
    /// same Conversation record - this is the "Customer 360" unification promise.
    /// </summary>
    Task<Domain.Conversation.Conversation> GetOrCreateConversationAsync(Guid tenantId, Guid customerId, ConversationChannelType channelType, CancellationToken ct = default);

    Task<Interaction> StartInteractionAsync(Guid tenantId, Guid conversationId, ConversationChannelType channelType, Guid? callSessionId, CancellationToken ct = default);
    Task<Message> PostMessageAsync(Guid tenantId, PostMessageRequest request, CancellationToken ct = default);
    Task AssignAsync(Guid tenantId, Guid conversationId, Guid agentId, string reason, CancellationToken ct = default);
    Task<List<UnifiedInboxItemDto>> GetUnifiedInboxAsync(Guid tenantId, Guid agentId, CancellationToken ct = default);
}

public class ConversationService : IConversationService
{
    private readonly IRepository<Domain.Conversation.Conversation> _conversations;
    private readonly IRepository<Interaction> _interactions;
    private readonly IRepository<Message> _messages;
    private readonly IRepository<Assignment> _assignments;
    private readonly IUnitOfWork _unitOfWork;

    public ConversationService(
        IRepository<Domain.Conversation.Conversation> conversations,
        IRepository<Interaction> interactions,
        IRepository<Message> messages,
        IRepository<Assignment> assignments,
        IUnitOfWork unitOfWork)
    {
        _conversations = conversations;
        _interactions = interactions;
        _messages = messages;
        _assignments = assignments;
        _unitOfWork = unitOfWork;
    }

    public async Task<Domain.Conversation.Conversation> GetOrCreateConversationAsync(
        Guid tenantId, Guid customerId, ConversationChannelType channelType, CancellationToken ct = default)
    {
        var existing = await _conversations.FirstOrDefaultAsync(
            c => c.TenantId == tenantId && c.CustomerId == customerId && c.Status != ConversationStatus.Closed, ct);
        if (existing is not null) return existing;

        var conversation = new Domain.Conversation.Conversation
        {
            TenantId = tenantId,
            CustomerId = customerId,
            ChannelType = channelType,
            Status = ConversationStatus.Open
        };
        await _conversations.AddAsync(conversation, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return conversation;
    }

    public async Task<Interaction> StartInteractionAsync(
        Guid tenantId, Guid conversationId, ConversationChannelType channelType, Guid? callSessionId, CancellationToken ct = default)
    {
        var interaction = new Interaction
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            ChannelType = channelType,
            CallSessionId = callSessionId
        };
        await _interactions.AddAsync(interaction, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return interaction;
    }

    public async Task<Message> PostMessageAsync(Guid tenantId, PostMessageRequest request, CancellationToken ct = default)
    {
        // Idempotency guard against redelivered webhooks (Section 13 - Inbox pattern):
        // a provider message id we've already stored should never be inserted twice.
        if (!string.IsNullOrEmpty(request.ProviderMessageId))
        {
            var duplicate = await _messages.AnyAsync(m => m.ProviderMessageId == request.ProviderMessageId, ct);
            if (duplicate)
                throw new InvalidOperationException("Duplicate provider message - ignored.");
        }

        var message = new Message
        {
            TenantId = tenantId,
            InteractionId = request.ConversationId, // TODO: replace with the correct active InteractionId lookup
            Direction = request.Direction,
            Body = request.Body,
            SenderAgentId = request.SenderAgentId,
            ProviderMessageId = request.ProviderMessageId
        };
        await _messages.AddAsync(message, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return message;
    }

    public async Task AssignAsync(Guid tenantId, Guid conversationId, Guid agentId, string reason, CancellationToken ct = default)
    {
        var conversation = await _conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Conversation not found.");

        conversation.AssignedAgentId = agentId;
        _conversations.Update(conversation);

        await _assignments.AddAsync(new Assignment
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            AgentId = agentId,
            Reason = reason
        }, ct);

        await _unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<List<UnifiedInboxItemDto>> GetUnifiedInboxAsync(Guid tenantId, Guid agentId, CancellationToken ct = default)
    {
        var items = await _conversations.ToListAsync(
            _conversations.Query().Where(c => c.TenantId == tenantId && c.AssignedAgentId == agentId), ct);

        return items.Select(c => new UnifiedInboxItemDto(c.Id, c.ChannelType, c.Status, c.AssignedAgentId, c.UpdatedAt)).ToList();
    }
}
