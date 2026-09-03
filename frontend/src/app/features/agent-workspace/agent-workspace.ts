import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, computed, OnDestroy, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

interface UnifiedInboxItem {
  conversationId: string;
  channelType: string;
  status: string;
  assignedAgentId: string | null;
  updatedAt: string;
}

interface HandoffItem {
  id: string; callSessionId: string; fromNumber: string; toNumber: string;
  occurredAt: string; payloadJson: string | null; contextJson: string | null;
  callStatus: string; endedAt: string | null;
  customerId: string | null; dispositionId: string | null; fullRecordingId: string | null;
  handoffOutcome: { eventType: string; payloadJson: string | null; occurredAt: string } | null;
}

interface DispositionItem { id: string; code: string; description: string; requiresFollowUp: boolean; }

interface HandoffContext {
  summary?: string; customerIntent?: string; detectedLanguage?: string; sentiment?: string;
  collectedDetails?: string[]; unresolvedItems?: string[]; suggestedOpening?: string;
  transcript?: string; provider?: string; usedFallback?: boolean;
}

/**
 * Section 11 - Agent Workspace: "Unified inbox across voice and digital channels; embedded
 * WebRTC phone; Customer 360 panel; campaign script and lead context; disposition and
 * wrap-up timer; follow-up/callback scheduling; suggested replies/AI summary."
 *
 * This starter only wires up the unified inbox list against GET /api/conversations/inbox.
 * The WebRTC phone (SIP.js) panel, Customer 360 panel, and wrap-up/disposition controls are
 * intentionally left as TODOs - see Section 8 for the SIP.js + WebRTC call flow to build
 * against once the Telephony worker's ARI event handling is filled in.
 */
@Component({
  selector: 'app-agent-workspace',
  imports: [DatePipe, FormsModule],
  templateUrl: './agent-workspace.html',
  styleUrl: './agent-workspace.scss',
})
export class AgentWorkspace implements OnInit, OnDestroy {
  readonly inbox = signal<UnifiedInboxItem[]>([]);
  readonly handoffs = signal<HandoffItem[]>([]);
  readonly activeHandoffs = computed(() => this.handoffs().filter(x => this.isActive(x)));
  readonly recentHandoffs = computed(() => this.handoffs().filter(x => !this.isActive(x)));
  readonly visibleHandoffs = computed(() => [...this.activeHandoffs(), ...this.recentHandoffs()]);
  readonly loading = signal(false);
  readonly handoffRefreshWarning = signal('');
  readonly dispositions = signal<DispositionItem[]>([]);
  readonly selectedCallId = signal('');
  readonly customer360 = signal<any>(null);
  readonly actionMessage = signal('');
  noteText = '';
  dispositionId = '';
  private timer?: number;

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void {
    this.loadInbox();
    this.loadHandoffs();
    this.loadDispositions();
    this.timer = window.setInterval(() => this.loadHandoffs(), 2500);
  }

  loadDispositions(): void {
    this.http.get<DispositionItem[]>(`${environment.apiBaseUrl}/calls/dispositions`)
      .subscribe({ next: values => this.dispositions.set(values) });
  }

  async selectCall(item: HandoffItem): Promise<void> {
    this.selectedCallId.set(item.callSessionId);
    this.dispositionId = item.dispositionId ?? '';
    this.actionMessage.set('');
    try {
      this.customer360.set(await firstValueFrom(this.http.get<any>(
        `${environment.apiBaseUrl}/calls/${item.callSessionId}/customer-360`)));
    } catch { this.customer360.set(null); }
  }

  async saveWrapUp(): Promise<void> {
    const callId = this.selectedCallId();
    if (!callId) return;
    try {
      if (this.noteText.trim()) await firstValueFrom(this.http.post(
        `${environment.apiBaseUrl}/calls/${callId}/notes`, { body: this.noteText.trim() }));
      if (this.dispositionId) await firstValueFrom(this.http.put(
        `${environment.apiBaseUrl}/calls/${callId}/disposition?dispositionId=${encodeURIComponent(this.dispositionId)}`, null));
      this.noteText = '';
      this.actionMessage.set('Wrap-up saved.');
      const item = this.handoffs().find(x => x.callSessionId === callId);
      if (item) await this.selectCall({ ...item, dispositionId: this.dispositionId });
      this.loadHandoffs();
    } catch (error: any) { this.actionMessage.set(error?.error?.message ?? 'Could not save wrap-up.'); }
  }

  async downloadRecording(item: HandoffItem): Promise<void> {
    if (!item.fullRecordingId) return;
    const blob = await firstValueFrom(this.http.get(
      `${environment.apiBaseUrl}/calls/recordings/${item.fullRecordingId}`, { responseType: 'blob' }));
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a'); anchor.href = url;
    anchor.download = `full-call-${item.callSessionId}.wav`; anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  ngOnDestroy(): void { if (this.timer) window.clearInterval(this.timer); }

  loadInbox(): void {
    this.loading.set(true);
    // TODO: pass the real signed-in agentId once Auth exposes decoded JWT claims.
    this.http
      .get<UnifiedInboxItem[]>(`${environment.apiBaseUrl}/conversations/inbox`)
      .subscribe({
        next: (items) => this.inbox.set(items),
        error: () => this.inbox.set([]),
        complete: () => this.loading.set(false)
      });
  }


  loadHandoffs(): void {
    this.http.get<HandoffItem[]>(`${environment.apiBaseUrl}/calls/handoffs?take=25`)
      .subscribe({
        next: items => {
          this.handoffs.set(items);
          this.handoffRefreshWarning.set('');
        },
        // A temporary polling/auth/network failure must not erase the agent's
        // current screen-pop. Preserve the last successful snapshot and retry.
        error: () => this.handoffRefreshWarning.set('Live refresh delayed; showing the last successful handoff data.')
      });
  }

  extension(item: HandoffItem): string { return this.payload(item).extension ?? 'Not assigned'; }
  reason(item: HandoffItem): string { return this.payload(item).reason ?? 'Caller requested human assistance'; }
  state(item: HandoffItem): string { return this.isActive(item) ? 'Ringing / connected' : 'Completed'; }
  outcome(item: HandoffItem): string {
    if (!item.handoffOutcome) return '';
    try {
      const value = JSON.parse(item.handoffOutcome.payloadJson ?? '{}');
      return value.outcome ? `${value.outcome}${value.extension ? ` · ${value.extension}` : ''}` : item.handoffOutcome.eventType;
    } catch { return item.handoffOutcome.eventType; }
  }
  context(item: HandoffItem): HandoffContext {
    try {
      const value = JSON.parse(item.contextJson ?? '{}');
      // Older final-summary events were serialized directly from C# properties,
      // producing PascalCase keys. Accept both shapes so persisted summaries do
      // not disappear when polling replaces the preliminary event with the final one.
      return {
        summary: value.summary ?? value.Summary,
        customerIntent: value.customerIntent ?? value.CustomerIntent,
        detectedLanguage: value.detectedLanguage ?? value.DetectedLanguage,
        sentiment: value.sentiment ?? value.Sentiment,
        collectedDetails: value.collectedDetails ?? value.CollectedDetails,
        unresolvedItems: value.unresolvedItems ?? value.UnresolvedItems,
        suggestedOpening: value.suggestedOpening ?? value.SuggestedOpening,
        transcript: value.transcript ?? value.Transcript,
        provider: value.provider ?? value.Provider,
        usedFallback: value.usedFallback ?? value.UsedFallback
      };
    } catch { return {}; }
  }
  transcriptLines(item: HandoffItem): string[] {
    return String(this.context(item).transcript ?? '').split('\n').filter(Boolean).slice(-20);
  }

  private payload(item: HandoffItem): any {
    try { return JSON.parse(item.payloadJson ?? '{}'); } catch { return {}; }
  }

  private isActive(item: HandoffItem): boolean {
    return !item.endedAt && !['Ended', 'Failed', 'Abandoned'].includes(item.callStatus);
  }
}
