using CCaaS.Application.Common;
using CCaaS.Domain.Organization;

namespace CCaaS.Application.Organization;

public record CreateBranchRequest(string Name, string? Address);
public record CreateTeamRequest(string Name, Guid? BranchId);
public record CreateAgentRequest(Guid UserId, string DisplayName, Guid? TeamId, string? ExtensionNumber);
public record AgentDto(Guid Id, string DisplayName, string? ExtensionNumber, AgentPresence Presence);

public interface IOrganizationService
{
    Task<Branch> CreateBranchAsync(Guid tenantId, CreateBranchRequest request, CancellationToken ct = default);
    Task<Team> CreateTeamAsync(Guid tenantId, CreateTeamRequest request, CancellationToken ct = default);
    Task<AgentDto> CreateAgentAsync(Guid tenantId, CreateAgentRequest request, CancellationToken ct = default);
    Task SetAgentPresenceAsync(Guid tenantId, Guid agentId, AgentPresence presence, CancellationToken ct = default);
    Task<List<AgentDto>> ListAgentsAsync(Guid tenantId, CancellationToken ct = default);
}

public class OrganizationService : IOrganizationService
{
    private readonly IRepository<Branch> _branches;
    private readonly IRepository<Team> _teams;
    private readonly IRepository<Agent> _agents;
    private readonly IUnitOfWork _unitOfWork;

    public OrganizationService(IRepository<Branch> branches, IRepository<Team> teams, IRepository<Agent> agents, IUnitOfWork unitOfWork)
    {
        _branches = branches;
        _teams = teams;
        _agents = agents;
        _unitOfWork = unitOfWork;
    }

    public async Task<Branch> CreateBranchAsync(Guid tenantId, CreateBranchRequest request, CancellationToken ct = default)
    {
        var branch = new Branch { TenantId = tenantId, Name = request.Name, Address = request.Address };
        await _branches.AddAsync(branch, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return branch;
    }

    public async Task<Team> CreateTeamAsync(Guid tenantId, CreateTeamRequest request, CancellationToken ct = default)
    {
        var team = new Team { TenantId = tenantId, Name = request.Name, BranchId = request.BranchId };
        await _teams.AddAsync(team, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return team;
    }

    public async Task<AgentDto> CreateAgentAsync(Guid tenantId, CreateAgentRequest request, CancellationToken ct = default)
    {
        var agent = new Agent
        {
            TenantId = tenantId,
            UserId = request.UserId,
            DisplayName = request.DisplayName,
            TeamId = request.TeamId,
            ExtensionNumber = request.ExtensionNumber,
            Presence = AgentPresence.Offline
        };
        await _agents.AddAsync(agent, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return new AgentDto(agent.Id, agent.DisplayName, agent.ExtensionNumber, agent.Presence);
    }

    public async Task SetAgentPresenceAsync(Guid tenantId, Guid agentId, AgentPresence presence, CancellationToken ct = default)
    {
        var agent = await _agents.FirstOrDefaultAsync(a => a.Id == agentId && a.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Agent not found.");

        agent.Presence = presence;
        agent.PresenceChangedAt = DateTime.UtcNow;
        _agents.Update(agent);
        await _unitOfWork.SaveChangesAsync(ct);

        // TODO: broadcast presence change over SignalR (Supervisor Center realtime dashboard,
        // Section 11) - inject IHubContext-backed notifier abstraction here once the Api
        // project's SignalR hub is wired in.
    }

    public async Task<List<AgentDto>> ListAgentsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var agents = await _agents.ToListAsync(_agents.Query().Where(a => a.TenantId == tenantId), ct);
        return agents.Select(a => new AgentDto(a.Id, a.DisplayName, a.ExtensionNumber, a.Presence)).ToList();
    }
}
