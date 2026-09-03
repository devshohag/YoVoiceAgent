using CCaaS.Application.Common;
using CCaaS.Domain.Ai;

namespace CCaaS.Application.Ai;

public record CreateAiAgentRequest(
    string Name,
    string Language,
    string VoiceName,
    string SystemPrompt,
    string WelcomeMessage,
    Guid? HumanQueueId,
    bool AllowInboundCalls = true,
    bool AllowOutboundCalls = false);

public record ProcessRecordingRequest(Guid AiAgentId, Guid? CallSessionId, Guid? CustomerId, string RecordingObjectStorageKey);
public record ExecuteAiToolRequest(string ToolName, string ArgumentsJson, bool Confirmed);
public record StartAiTestConversationRequest(Guid AiAgentId);
public record AddAiConversationTurnRequest(AiSpeaker Speaker, string Text);

public interface IAiAgentService
{
    Task<AiAgent> CreateAgentAsync(Guid tenantId, CreateAiAgentRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<AiAgent>> GetAgentsAsync(CancellationToken ct = default);
    Task<AiConversation> ProcessRecordingAsync(Guid tenantId, ProcessRecordingRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<AiConversation>> GetConversationsAsync(CancellationToken ct = default);
    Task<AiConversation?> GetConversationAsync(Guid id, CancellationToken ct = default);
    Task<AiConversation> StartTestConversationAsync(Guid tenantId, Guid aiAgentId, CancellationToken ct = default);
    Task<AiConversationTurn> AddTurnAsync(Guid tenantId, Guid conversationId, AddAiConversationTurnRequest request, CancellationToken ct = default);
    Task<AiToolExecution> ExecuteToolAsync(Guid tenantId, Guid conversationId, ExecuteAiToolRequest request, CancellationToken ct = default);
    Task<AiConversation> RequestHandoffAsync(Guid tenantId, Guid conversationId, string reason, CancellationToken ct = default);
}

public sealed class AiAgentService : IAiAgentService
{
    private readonly IRepository<AiAgent> _agents;
    private readonly IRepository<AiAgentVersion> _versions;
    private readonly IRepository<AiConversation> _conversations;
    private readonly IRepository<AiConversationTurn> _turns;
    private readonly IRepository<AiToolExecution> _toolExecutions;
    private readonly IRepository<AiUsageRecord> _usage;
    private readonly IUnitOfWork _uow;
    private readonly ISpeechToTextProvider _speechToText;
    private readonly ISummaryProvider _summary;
    private readonly IQaScoringProvider _qa;
    private readonly ISentimentProvider _sentiment;
    private readonly ITranscriptRedactor _redactor;
    private readonly IReadOnlyDictionary<string, IAiToolHandler> _tools;

    public AiAgentService(
        IRepository<AiAgent> agents,
        IRepository<AiAgentVersion> versions,
        IRepository<AiConversation> conversations,
        IRepository<AiConversationTurn> turns,
        IRepository<AiToolExecution> toolExecutions,
        IRepository<AiUsageRecord> usage,
        IUnitOfWork uow,
        ISpeechToTextProvider speechToText,
        ISummaryProvider summary,
        IQaScoringProvider qa,
        ISentimentProvider sentiment,
        ITranscriptRedactor redactor,
        IEnumerable<IAiToolHandler> tools)
    {
        _agents = agents;
        _versions = versions;
        _conversations = conversations;
        _turns = turns;
        _toolExecutions = toolExecutions;
        _usage = usage;
        _uow = uow;
        _speechToText = speechToText;
        _summary = summary;
        _qa = qa;
        _sentiment = sentiment;
        _redactor = redactor;
        _tools = tools.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AiAgent> CreateAgentAsync(Guid tenantId, CreateAiAgentRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.SystemPrompt))
            throw new ArgumentException("Agent name and system prompt are required.");

        var agent = new AiAgent
        {
            TenantId = tenantId,
            Name = request.Name.Trim(),
            Language = request.Language,
            VoiceName = request.VoiceName,
            SystemPrompt = request.SystemPrompt.Trim(),
            WelcomeMessage = request.WelcomeMessage.Trim(),
            HumanQueueId = request.HumanQueueId,
            AllowInboundCalls = request.AllowInboundCalls,
            AllowOutboundCalls = request.AllowOutboundCalls
        };

        await _agents.AddAsync(agent, ct);
        await _versions.AddAsync(new AiAgentVersion
        {
            TenantId = tenantId,
            AiAgentId = agent.Id,
            Version = 1,
            ConfigurationJson = System.Text.Json.JsonSerializer.Serialize(request)
        }, ct);
        await _uow.SaveChangesAsync(ct);
        return agent;
    }

    public async Task<IReadOnlyList<AiAgent>> GetAgentsAsync(CancellationToken ct = default)
        => await _agents.ToListAsync(_agents.Query().OrderBy(x => x.Name), ct);

    public async Task<IReadOnlyList<AiConversation>> GetConversationsAsync(CancellationToken ct = default)
        => await _conversations.ToListAsync(_conversations.Query().OrderByDescending(x => x.StartedAt), ct);

    public Task<AiConversation?> GetConversationAsync(Guid id, CancellationToken ct = default)
        => _conversations.GetByIdAsync(id, ct);

    public async Task<AiConversation> StartTestConversationAsync(Guid tenantId, Guid aiAgentId, CancellationToken ct = default)
    {
        var agent = await _agents.GetByIdAsync(aiAgentId, ct)
            ?? throw new KeyNotFoundException("AI agent was not found.");
        if (!agent.IsActive) throw new InvalidOperationException("AI agent is disabled.");

        var conversation = new AiConversation
        {
            TenantId = tenantId,
            AiAgentId = agent.Id,
            Status = AiConversationStatus.Processing,
            DetectedLanguage = agent.Language
        };
        await _conversations.AddAsync(conversation, ct);
        await _turns.AddAsync(new AiConversationTurn
        {
            TenantId = tenantId,
            AiConversationId = conversation.Id,
            Speaker = AiSpeaker.AiAgent,
            Text = agent.WelcomeMessage,
            Sequence = 1
        }, ct);
        await _uow.SaveChangesAsync(ct);
        return conversation;
    }

    public async Task<AiConversationTurn> AddTurnAsync(Guid tenantId, Guid conversationId, AddAiConversationTurnRequest request, CancellationToken ct = default)
    {
        _ = await _conversations.GetByIdAsync(conversationId, ct)
            ?? throw new KeyNotFoundException("AI conversation was not found.");
        if (request.Speaker is not (AiSpeaker.Customer or AiSpeaker.AiAgent or AiSpeaker.System))
            throw new ArgumentException("Only customer, AI-agent, and system turns are accepted here.");
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Turn text is required.");

        var existing = await _turns.ToListAsync(_turns.Query().Where(x => x.AiConversationId == conversationId), ct);
        var turn = new AiConversationTurn
        {
            TenantId = tenantId,
            AiConversationId = conversationId,
            Speaker = request.Speaker,
            Text = request.Text.Trim(),
            Sequence = existing.Count + 1
        };
        await _turns.AddAsync(turn, ct);
        await _uow.SaveChangesAsync(ct);
        return turn;
    }

    public async Task<AiConversation> ProcessRecordingAsync(Guid tenantId, ProcessRecordingRequest request, CancellationToken ct = default)
    {
        var agent = await _agents.GetByIdAsync(request.AiAgentId, ct)
            ?? throw new KeyNotFoundException("AI agent was not found.");
        if (!agent.IsActive) throw new InvalidOperationException("AI agent is disabled.");

        var conversation = new AiConversation
        {
            TenantId = tenantId,
            AiAgentId = agent.Id,
            CallSessionId = request.CallSessionId,
            CustomerId = request.CustomerId,
            Status = AiConversationStatus.Processing
        };
        await _conversations.AddAsync(conversation, ct);
        await _uow.SaveChangesAsync(ct);

        try
        {
            var transcript = await _speechToText.TranscribeAsync(request.RecordingObjectStorageKey, ct);
            var redacted = _redactor.Redact(transcript.Text);
            var summary = await _summary.SummarizeAsync(redacted, ct);
            var sentiment = await _sentiment.AnalyzeAsync(redacted, ct);
            var qa = await _qa.ScoreAsync(redacted, "{\"checks\":[\"greeting\",\"verification\",\"resolution\"]}", ct);

            conversation.Transcript = transcript.Text;
            conversation.RedactedTranscript = redacted;
            conversation.Summary = summary.Summary;
            conversation.SuggestedDisposition = summary.SuggestedDisposition;
            conversation.SuggestedFollowUp = summary.SuggestedFollowUp;
            conversation.Sentiment = sentiment.Label;
            conversation.QaScore = qa.Score;
            conversation.Status = AiConversationStatus.Completed;
            conversation.EndedAt = DateTime.UtcNow;

            await _turns.AddAsync(new AiConversationTurn
            {
                TenantId = tenantId,
                AiConversationId = conversation.Id,
                Speaker = AiSpeaker.Customer,
                Text = redacted,
                Sequence = 1
            }, ct);
            await _usage.AddAsync(new AiUsageRecord
            {
                TenantId = tenantId,
                AiConversationId = conversation.Id,
                Provider = "configured-provider",
                Operation = "post-call-intelligence",
                InputUnits = redacted.Length,
                OutputUnits = summary.Summary.Length
            }, ct);
        }
        catch (Exception ex)
        {
            conversation.Status = AiConversationStatus.Failed;
            conversation.Outcome = ex.Message[..Math.Min(ex.Message.Length, 500)];
            conversation.EndedAt = DateTime.UtcNow;
        }

        _conversations.Update(conversation);
        await _uow.SaveChangesAsync(ct);
        return conversation;
    }

    public async Task<AiToolExecution> ExecuteToolAsync(Guid tenantId, Guid conversationId, ExecuteAiToolRequest request, CancellationToken ct = default)
    {
        _ = await _conversations.GetByIdAsync(conversationId, ct)
            ?? throw new KeyNotFoundException("AI conversation was not found.");

        var execution = new AiToolExecution
        {
            TenantId = tenantId,
            AiConversationId = conversationId,
            ToolName = request.ToolName,
            ArgumentsJson = request.ArgumentsJson,
            WasConfirmed = request.Confirmed
        };
        var started = System.Diagnostics.Stopwatch.StartNew();

        if (!_tools.TryGetValue(request.ToolName, out var handler))
        {
            execution.Status = AiToolExecutionStatus.Rejected;
            execution.FailureReason = "Tool is not allowlisted.";
        }
        else if (!request.Confirmed)
        {
            execution.Status = AiToolExecutionStatus.Rejected;
            execution.FailureReason = "Human confirmation is required.";
        }
        else
        {
            try
            {
                execution.ResultJson = await handler.ExecuteAsync(tenantId, request.ArgumentsJson, ct);
                execution.Status = AiToolExecutionStatus.Succeeded;
            }
            catch (Exception ex)
            {
                execution.Status = AiToolExecutionStatus.Failed;
                execution.FailureReason = ex.Message[..Math.Min(ex.Message.Length, 500)];
            }
        }

        execution.DurationMilliseconds = (int)started.ElapsedMilliseconds;
        await _toolExecutions.AddAsync(execution, ct);
        await _uow.SaveChangesAsync(ct);
        return execution;
    }

    public async Task<AiConversation> RequestHandoffAsync(Guid tenantId, Guid conversationId, string reason, CancellationToken ct = default)
    {
        var conversation = await _conversations.GetByIdAsync(conversationId, ct)
            ?? throw new KeyNotFoundException("AI conversation was not found.");
        conversation.Status = AiConversationStatus.HandoffRequested;
        conversation.HandoffReason = string.IsNullOrWhiteSpace(reason) ? "Customer requested a human agent." : reason.Trim();
        _conversations.Update(conversation);
        await _uow.SaveChangesAsync(ct);
        return conversation;
    }
}
