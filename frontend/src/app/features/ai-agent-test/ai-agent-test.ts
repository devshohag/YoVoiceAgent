import { HttpClient } from '@angular/common/http';
import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { PuterAi, PuterChatMessage, PuterToolDefinition } from '../../core/ai/puter-ai';
import { environment } from '../../../environments/environment';
import { AiAgent } from '../ai-agents/ai-agents';

interface ChatBubble { role: 'assistant' | 'user' | 'tool'; content: string; }
interface BookingConfirmation {
  bookingId: string;
  bookingReference: string;
  startsAtUtc: string;
  status: string;
}

const APPOINTMENT_TOOLS: PuterToolDefinition[] = [
  { type: 'function', function: { name: 'check_appointment_availability', description: 'Check available appointment slots for a date before offering a time.', parameters: { type: 'object', properties: { date: { type: 'string', description: 'Date in YYYY-MM-DD format' } }, required: ['date'] } } },
  { type: 'function', function: { name: 'book_appointment', description: 'Book a SQL-backed appointment only after the customer explicitly confirms. Use the exact slotId returned by check_appointment_availability.', parameters: { type: 'object', properties: { slotId: { type: 'string', description: 'Exact availability slot GUID returned by the availability tool' }, customerName: { type: 'string' }, contact: { type: 'string' }, purpose: { type: 'string' } }, required: ['slotId', 'customerName', 'contact'] } } },
  { type: 'function', function: { name: 'request_human_handoff', description: 'Request a human when the customer asks, the request is unsupported, or the AI is uncertain.', parameters: { type: 'object', properties: { reason: { type: 'string' } }, required: ['reason'] } } }
];

@Component({
  selector: 'app-ai-agent-test', standalone: true, imports: [FormsModule],
  templateUrl: './ai-agent-test.html', styleUrl: './ai-agent-test.scss'
})
export class AiAgentTest implements OnInit {
  readonly agents = signal<AiAgent[]>([]);
  readonly conversation = signal<ChatBubble[]>([]);
  readonly loading = signal(false);
  readonly transcribing = signal(false);
  readonly speaking = signal(false);
  readonly status = signal('Ready');
  readonly replyAudioUrl = signal('');
  readonly conversationId = signal('');
  readonly error = signal('');
  readonly bookingConfirmation = signal<BookingConfirmation | null>(null);
  selectedAgentId = '';
  customerMessage = 'I need an appointment tomorrow afternoon.';
  autoProcessAudio = true;
  autoSpeakReply = true;
  private currentAudio?: HTMLAudioElement;
  private toolContext: string[] = [];
  private bookingAuthorized = false;

  constructor(private readonly http: HttpClient, private readonly puterAi: PuterAi,
    private readonly route: ActivatedRoute) {}

  async ngOnInit(): Promise<void> {
    try {
      const agents = await firstValueFrom(this.http.get<AiAgent[]>(`${environment.apiBaseUrl}/ai-agents`));
      this.agents.set(agents.filter(agent => agent.isActive));
      const requestedId = this.route.snapshot.queryParamMap.get('agentId');
      this.selectedAgentId = agents.some(agent => agent.id === requestedId) ? requestedId! : agents[0]?.id ?? '';
      await this.startNewConversation();
    } catch { this.error.set('Could not load AI agents. Please log in again.'); }
  }

  get selectedAgent(): AiAgent | undefined {
    return this.agents().find(agent => agent.id === this.selectedAgentId);
  }

  resetConversation(): void { void this.startNewConversation(); }

  private async startNewConversation(): Promise<void> {
    const agent = this.selectedAgent;
    this.currentAudio?.pause();
    this.currentAudio = undefined;
    this.replyAudioUrl.set('');
    this.error.set('');
    this.bookingConfirmation.set(null);
    this.toolContext = [];
    this.bookingAuthorized = false;
    this.status.set('Ready');
    this.conversation.set(agent ? [{ role: 'assistant', content: agent.welcomeMessage }] : []);
    this.conversationId.set('');
    if (!agent) return;
    try {
      const conversation: any = await firstValueFrom(this.http.post(
        `${environment.apiBaseUrl}/ai-conversations/test`, { aiAgentId: agent.id }
      ));
      this.conversationId.set(conversation.id);
    } catch (error) {
      this.error.set(this.describeError(error));
      this.status.set('Could not start audited conversation');
    }
  }

  async send(): Promise<void> {
    const text = this.customerMessage.trim();
    if (!text) return;
    this.customerMessage = '';
    await this.processCustomerMessage(text);
  }

  private async processCustomerMessage(text: string): Promise<void> {
    const agent = this.selectedAgent;
    if (!agent || !text || this.loading()) return;
    if (!this.conversationId()) await this.startNewConversation();
    if (!this.conversationId()) return;
    this.error.set('');
    if (/\b(yes|confirm|confirmed|book|booking|go ahead|proceed)\b/i.test(text))
      this.bookingAuthorized = true;
    this.conversation.update(items => [...items, { role: 'user', content: text }]);
    await this.saveTurn(0, text);
    this.loading.set(true);
    this.status.set('Generating AI response…');
    try {
      const messages: PuterChatMessage[] = [
        { role: 'system', content: `${agent.systemPrompt}\n\nToday is ${new Date().toISOString().slice(0, 10)}. This is a voice conversation. Ask only one question at a time and keep every response under two short sentences. Use the availability tool before offering slots. Keep the exact slotId associated with each offered time. Collect customer name and contact. Never claim a booking succeeded unless book_appointment returned success. Obtain explicit confirmation before booking.` },
        ...(this.toolContext.length ? [{ role: 'system' as const, content: `Approved tool results from this conversation:\n${this.toolContext.join('\n')}` }] : []),
        ...this.conversation().filter(item => item.role !== 'tool')
          .map(item => ({ role: item.role, content: item.content } as PuterChatMessage))
      ];
      const reply = await this.puterAi.chatWithTools(
        messages, APPOINTMENT_TOOLS,
        (name, argumentsJson) => this.executeApprovedTool(name, argumentsJson, text),
        tool => {
          this.toolContext.push(`${tool.name}: ${tool.resultJson}`);
          this.toolContext = this.toolContext.slice(-6);
          this.captureBooking(tool.name, tool.resultJson);
          const display = `${tool.name}: ${this.describeToolResult(tool.resultJson)}`;
          this.conversation.update(items => [...items, { role: 'tool', content: display }]);
          void this.saveTurn(3, display);
        }
      );
      this.conversation.update(items => [...items, { role: 'assistant', content: reply }]);
      await this.saveTurn(1, reply);
      this.status.set('AI response ready');
      if (this.autoSpeakReply) await this.playReply(reply, agent.language, true);
    } catch (error) {
      this.error.set(this.describeError(error));
      this.status.set('Processing failed');
    } finally { this.loading.set(false); }
  }

  private async executeApprovedTool(name: string, argumentsJson: string, latestCustomerText: string): Promise<string> {
    const confirmed = name !== 'book_appointment' || this.bookingAuthorized
      || /\b(yes|confirm|confirmed|book|booking|go ahead|proceed)\b/i.test(latestCustomerText);
    const execution: any = await firstValueFrom(this.http.post(
      `${environment.apiBaseUrl}/ai-conversations/${this.conversationId()}/tools`,
      { toolName: name, argumentsJson, confirmed }
    ));
    if (execution.resultJson) return execution.resultJson;
    return JSON.stringify({ success: false, status: 'rejected', reason: execution.failureReason ?? 'Tool execution failed.' });
  }

  private async saveTurn(speaker: number, text: string): Promise<void> {
    if (!this.conversationId()) return;
    try {
      await firstValueFrom(this.http.post(
        `${environment.apiBaseUrl}/ai-conversations/${this.conversationId()}/turns`, { speaker, text }
      ));
    } catch (error) { console.error('Could not persist AI conversation turn:', error); }
  }

  private describeToolResult(resultJson: string): string {
    try {
      const result = JSON.parse(resultJson);
      if (result.bookingReference) return `confirmed as ${result.bookingReference}`;
      if (result.slots) return `available slots ${result.slots.map((slot: any) => slot.displayTime ?? slot.startsAtUtc).join(', ')}`;
      if (result.reason) return result.reason;
      return result.status ?? 'completed';
    } catch { return resultJson; }
  }

  private captureBooking(toolName: string, resultJson: string): void {
    if (toolName !== 'book_appointment') return;
    try {
      const result = JSON.parse(resultJson);
      if (result.success && result.bookingId && result.bookingReference) {
        this.bookingAuthorized = false;
        this.bookingConfirmation.set({
          bookingId: result.bookingId,
          bookingReference: result.bookingReference,
          startsAtUtc: result.startsAtUtc,
          status: result.status ?? 'Confirmed'
        });
      }
    } catch { /* The tool error remains visible in the audited tool bubble. */ }
  }

  async downloadVoucher(): Promise<void> {
    const booking = this.bookingConfirmation();
    if (!booking) return;
    this.error.set('');
    this.status.set('Preparing booking voucher…');
    try {
      const blob = await firstValueFrom(this.http.get(
        `${environment.apiBaseUrl}/appointments/${booking.bookingId}/voucher`,
        { responseType: 'blob' }
      ));
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `Appointment-${booking.bookingReference}.pdf`;
      anchor.click();
      URL.revokeObjectURL(url);
      this.status.set('Booking voucher downloaded');
    } catch (error) {
      this.error.set(this.describeError(error));
      this.status.set('Voucher download failed');
    }
  }

  async speakLastReply(): Promise<void> {
    const agent = this.selectedAgent;
    const reply = [...this.conversation()].reverse().find(item => item.role === 'assistant');
    if (!agent || !reply) return;
    await this.playReply(reply.content, agent.language, true);
  }

  private async playReply(text: string, language: string, autoplay: boolean): Promise<void> {
    this.speaking.set(true);
    this.status.set('Creating voice response…');
    try {
      this.currentAudio?.pause();
      const audio = await this.puterAi.createSpeech(text, language);
      this.currentAudio = audio;
      this.replyAudioUrl.set(audio.src);
      this.status.set('Voice response ready');
      if (autoplay) {
        try { await audio.play(); }
        catch { this.status.set('Voice ready — press Play if browser blocked autoplay'); }
      }
    } catch (error) { this.error.set(this.describeError(error)); }
    finally { this.speaking.set(false); }
  }

  async uploadAudio(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    const agent = this.selectedAgent;
    if (!file || !agent) return;
    this.error.set(''); this.transcribing.set(true); this.status.set('Transcribing customer audio…');
    try {
      const transcript = await this.puterAi.transcribe(file, agent.language);
      this.customerMessage = transcript;
      this.transcribing.set(false);
      this.status.set('Transcription ready');
      if (this.autoProcessAudio) {
        this.customerMessage = '';
        await this.processCustomerMessage(transcript);
      }
    }
    catch (error) { this.error.set(this.describeError(error)); this.status.set('Audio processing failed'); }
    finally { this.transcribing.set(false); input.value = ''; }
  }

  private describeError(error: unknown): string {
    console.error('Puter operation failed:', error);
    if (typeof error === 'string') return error;
    if (error instanceof Error) return error.message;
    const data = error as any;
    const message = data?.message ?? data?.error?.message ?? data?.error?.description
      ?? data?.description ?? data?.details?.message;
    if (message) return String(message);
    try { return JSON.stringify(error); }
    catch { return 'Puter AI is unavailable. Sign in to Puter and try again.'; }
  }
}
