using CCaaS.Application.Common;
using CCaaS.Domain.Crm;

namespace CCaaS.Application.Crm;

public record CreateCustomerRequest(string Name, string? Phone, string? Email);
public record AddNoteRequest(Guid CustomerId, Guid AuthorAgentId, string Body);
public record Customer360Dto(Guid Id, string Name, string? Phone, string? Email, List<string> Notes, List<string> Tags);

public interface ICrmService
{
    Task<Customer> CreateCustomerAsync(Guid tenantId, CreateCustomerRequest request, CancellationToken ct = default);
    Task<Customer?> FindByPhoneAsync(Guid tenantId, string phone, CancellationToken ct = default);
    Task<Customer360Dto?> GetCustomer360Async(Guid tenantId, Guid customerId, CancellationToken ct = default);
    Task AddNoteAsync(Guid tenantId, AddNoteRequest request, CancellationToken ct = default);
}

public class CrmService : ICrmService
{
    private readonly IRepository<Customer> _customers;
    private readonly IRepository<Note> _notes;
    private readonly IRepository<Contact> _contacts;
    private readonly IUnitOfWork _unitOfWork;

    public CrmService(IRepository<Customer> customers, IRepository<Note> notes, IRepository<Contact> contacts, IUnitOfWork unitOfWork)
    {
        _customers = customers;
        _notes = notes;
        _contacts = contacts;
        _unitOfWork = unitOfWork;
    }

    public async Task<Customer> CreateCustomerAsync(Guid tenantId, CreateCustomerRequest request, CancellationToken ct = default)
    {
        var customer = new Customer { TenantId = tenantId, Name = request.Name, Phone = request.Phone, Email = request.Email };
        await _customers.AddAsync(customer, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        return customer;
    }

    /// <summary>
    /// Matches the Inbound call flow step "Customer is matched by phone number and a CallSession
    /// is opened" (Section 8) and the Inbound message flow "Customer Match" step (Section 9).
    /// </summary>
    public async Task<Customer?> FindByPhoneAsync(Guid tenantId, string phone, CancellationToken ct = default)
        => await _customers.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Phone == phone, ct);

    public async Task<Customer360Dto?> GetCustomer360Async(Guid tenantId, Guid customerId, CancellationToken ct = default)
    {
        var customer = await _customers.FirstOrDefaultAsync(c => c.Id == customerId && c.TenantId == tenantId, ct);
        if (customer is null) return null;

        var notes = await _notes.ToListAsync(_notes.Query().Where(n => n.CustomerId == customerId), ct);
        return new Customer360Dto(customer.Id, customer.Name, customer.Phone, customer.Email,
            notes.Select(n => n.Body).ToList(), new List<string>());
    }

    public async Task AddNoteAsync(Guid tenantId, AddNoteRequest request, CancellationToken ct = default)
    {
        await _notes.AddAsync(new Note
        {
            TenantId = tenantId,
            CustomerId = request.CustomerId,
            AuthorAgentId = request.AuthorAgentId,
            Body = request.Body
        }, ct);
        await _unitOfWork.SaveChangesAsync(ct);
    }
}
