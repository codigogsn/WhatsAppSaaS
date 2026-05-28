using System.Threading;
using System.Threading.Tasks;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Interfaces;

// Phase 1 memory foundation: write-only durable per-message log.
// All implementations MUST swallow exceptions internally and log a warning
// so the caller can fire-and-forget without try/catch at every site.
// Logging is best-effort; the production message flow must never break
// because the memory log failed.
public interface IConversationMessageStore
{
    Task AppendAsync(ConversationMessage message, CancellationToken ct = default);
}
